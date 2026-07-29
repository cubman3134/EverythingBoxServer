using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Debrid;
using EverythingBox.Server.Core.Selection;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace EverythingBox.Server.Download;

/// <summary>
/// The first (and so far only) implementation of <see cref="ITorrentDownloader"/>:
/// fetches a release directly via an in-process BitTorrent engine (MonoTorrent)
/// rather than waiting on a debrid service to cache it.
/// <para>
/// Deliberately thin: everything decidable without a live swarm — whether there is
/// anything to fetch, which files to want, and cancellation — lives in code that can
/// be (and is) unit-tested. This class's own job is just: build a <see cref="MagnetLink"/>,
/// start the engine, deselect the files <see cref="MediaFileMatcher"/> didn't pick,
/// wait, and return paths. It takes no settings of its own — the size cap and the
/// timeout are the caller's business (see <see cref="ITorrentDownloader"/>); giving
/// this adapter its own copy would just be a second place for them to disagree.
/// </para>
/// </summary>
public sealed class MonoTorrentDownloader : ITorrentDownloader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<MonoTorrentDownloader> _logger;
    private readonly HttpClient _http;

    /// <summary>
    /// Takes an injected <see cref="HttpClient"/> (the shared one <c>Program.cs</c> builds
    /// over its shared <see cref="HttpMessageHandler"/>, so a <c>.torrent</c> fetch gets the
    /// same retry handling as every other outbound call) rather than constructing its own —
    /// construction stays allocation-free either way.
    /// </summary>
    public MonoTorrentDownloader(ILogger<MonoTorrentDownloader> logger, HttpClient http)
    {
        _logger = logger;
        _http = http;
    }

    public async Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent,
        MediaRequest? request,
        string directory,
        IProgress<TorrentDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Both checked before any I/O: a cancelled caller and a release with nothing
        // to fetch are ordinary, not errors, and neither should touch the filesystem.
        if (cancellationToken.IsCancellationRequested)
            return [];

        ClientEngine? engine = null;
        try
        {
            // Resolved inside the try so a mid-fetch failure (unreachable .torrent link,
            // a 404, non-torrent bytes, or cancellation) falls through to the same
            // empty-list-not-throw handling as every other failure below, rather than
            // needing its own copy of that logic.
            var magnetLink = await ResolveMagnetLinkAsync(torrent, cancellationToken).ConfigureAwait(false);
            if (magnetLink is null)
                return [];

            Directory.CreateDirectory(directory);

            // No trackers/DHT persistence between calls, no UPnP/local-discovery side
            // effects — this engine exists for one download and is torn down after.
            engine = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = directory,
                AllowPortForwarding = false,
                AllowLocalPeerDiscovery = false,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = false,
            }.ToSettings());

            var manager = await engine.AddAsync(magnetLink, directory).ConfigureAwait(false);

            // Blocks until the swarm hands over the torrent's file list, or the token
            // fires — a magnet with no peers must not hold the caller open forever.
            await manager.WaitForMetadataAsync(cancellationToken).ConfigureAwait(false);

            var wanted = SelectWantedFiles(manager, request);
            if (wanted.Count == 0)
            {
                _logger.LogInformation(
                    "No files in '{Title}' matched the request; nothing to download.", torrent.Title);
                return [];
            }

            await DeselectUnwantedAsync(manager, wanted).ConfigureAwait(false);
            await manager.StartAsync().ConfigureAwait(false);

            var totalBytes = wanted.Sum(f => f.Length);
            var completed = await WaitForCompletionAsync(manager, totalBytes, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!completed)
            {
                _logger.LogInformation(
                    "Self-download of '{Title}' gave up before finishing (no peers, a stalled swarm, or cancellation).",
                    torrent.Title);
                return [];
            }

            return wanted.Select(f => f.FullPath).ToList();
        }
        catch (OperationCanceledException)
        {
            // A caller-side cancellation (including the resolver's own timeout, applied
            // via a linked token) is a normal way for this to end, not a failure.
            _logger.LogInformation("Self-download of '{Title}' was cancelled.", torrent.Title);
            return [];
        }
        catch (Exception ex)
        {
            // No peers, a malformed magnet, a disk error, a stalled swarm — none of it
            // is exceptional from the caller's point of view. The caller treats an
            // empty result as "fall back to the notice".
            _logger.LogWarning(ex, "Self-download of '{Title}' failed.", torrent.Title);
            return [];
        }
        finally
        {
            // A leaked engine holds ports and threads; stop and release it on every
            // exit path, including the cancellation and failure ones above.
            if (engine is not null)
            {
                try
                {
                    await engine.StopAllAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort teardown — the engine is being discarded regardless.
                }

                engine.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="MagnetLink"/> from whatever the release gives us, via the same
    /// <see cref="MagnetResolver"/> the debrid path uses: the magnet URI directly, a bare
    /// magnet synthesized from the info hash, or — when all we have is a
    /// <see cref="TorrentResult.DownloadUrl"/> — one built by fetching the <c>.torrent</c>
    /// and reading its info hash. <see cref="MagnetResolver.ResolveAsync"/> already tries
    /// magnet/info-hash before ever touching HTTP, so a release with a usable magnet makes
    /// no network call here. Returns null (never throws) when nothing is present, when
    /// resolution fails (unreachable link, non-torrent bytes), or when what came back
    /// doesn't parse as a magnet.
    /// </summary>
    private async Task<MagnetLink?> ResolveMagnetLinkAsync(TorrentResult torrent, CancellationToken cancellationToken)
    {
        var magnet = await MagnetResolver.ResolveAsync(_http, torrent, cancellationToken).ConfigureAwait(false);
        if (magnet is null)
            return null;

        try
        {
            return MagnetLink.Parse(magnet);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build a magnet link for '{Title}'.", torrent.Title);
            return null;
        }
    }

    /// <summary>
    /// Which of the torrent's files to actually fetch, via the same
    /// <see cref="MediaFileMatcher"/> the debrid path uses — so a request for one
    /// episode doesn't pull a whole season pack. A null request (no media-type
    /// context to narrow by) takes everything.
    /// </summary>
    private static IReadOnlyList<ITorrentManagerFile> SelectWantedFiles(TorrentManager manager, MediaRequest? request)
    {
        var files = manager.Files.ToList();
        return request is null
            ? files
            : MediaFileMatcher.Select(request, files, f => f.Path, f => (long?)f.Length);
    }

    /// <summary>Tells the swarm not to fetch anything outside <paramref name="wanted"/>.</summary>
    private static async Task DeselectUnwantedAsync(TorrentManager manager, IReadOnlyList<ITorrentManagerFile> wanted)
    {
        var wantedSet = new HashSet<ITorrentManagerFile>(wanted);
        foreach (var file in manager.Files)
        {
            if (!wantedSet.Contains(file))
                await manager.SetFilePriorityAsync(file, Priority.DoNotDownload).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until the wanted files finish downloading, the swarm errors out, or the
    /// token fires. Uses <see cref="TorrentManager.PartialProgress"/> rather than
    /// <see cref="TorrentManager.Progress"/>, which only tracks the files we didn't
    /// deselect — the whole point of narrowing the selection in the first place.
    /// </summary>
    private async Task<bool> WaitForCompletionAsync(
        TorrentManager manager, long totalBytes, IProgress<TorrentDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (manager.PartialProgress >= 100.0)
                return true;

            if (manager.State == TorrentState.Error)
            {
                _logger.LogWarning(
                    manager.Error?.Exception, "Swarm for '{Name}' entered an error state: {Reason}",
                    manager.Name, manager.Error?.Reason);
                return false;
            }

            progress?.Report(new TorrentDownloadProgress(
                manager.Monitor.DataBytesReceived, totalBytes, manager.Monitor.DownloadRate));

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
