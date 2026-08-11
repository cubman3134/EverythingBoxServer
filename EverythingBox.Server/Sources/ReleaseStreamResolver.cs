using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Sources;

/// <summary>
/// Marks a request whose media type could not be determined — an id encoded before
/// the catalog's media type was recorded into it, or one from a source whose protocol
/// string <see cref="MediaTypeNames"/> doesn't recognize. <see cref="IDebridService"/>
/// still sees a normal, well-formed request (reporting <see cref="MediaType.Other"/>,
/// same as a plain <see cref="GeneralRequest"/> would); the only thing this distinct
/// type buys is that <see cref="ReleaseStreamResolver"/> can recognize it and skip
/// <see cref="MediaFileMatcher"/> entirely, rather than route it through
/// <c>MatchGeneral</c> — which scores a whole-torrent archive as the closest "title"
/// match (it has no extra tokens, unlike every real file) and would drop every real
/// file. Internal: constructed only by <c>IndexerSearchSource</c>, checked only here.
/// </summary>
internal sealed class UnknownMediaTypeRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Other;
}

/// <summary>
/// Turns a chosen <see cref="TorrentResult"/> into something the client can play, via
/// whatever <see cref="IDebridService"/> the host was configured with. When debrid
/// answers <see cref="DebridStatus.Pending"/> — it's still caching the release — and a
/// <see cref="ITorrentDownloader"/> was supplied, this optionally fetches the release
/// itself rather than leaving the caller with only a "try again shortly" notice; see
/// <see cref="TryFallbackDownloadAsync"/> for the gates that must all pass first.
/// </summary>
public sealed class ReleaseStreamResolver
{
    private readonly IDebridService? _debrid;
    private readonly ILogger<ReleaseStreamResolver> _logger;
    private readonly ITorrentDownloader? _downloader;
    private readonly IFileCache? _files;
    private readonly DownloadConfig _download;

    // Memoizes the one expensive step — actually joining the swarm — per (release, request),
    // independent of how many files it produces or how many indices a caller walks. Concurrent
    // callers asking for the SAME release+request share the SAME Lazy<Task<>>, so only the
    // caller that wins the dictionary insert ever runs RunDownloadAsync; everyone else just
    // awaits its result. Deliberately separate from IFileCache's own build-once dictionary,
    // which dedupes at the single-served-file granularity below in PublishAsync — this one
    // exists so that fetching index 0 and then index 1 of the same still-caching release (or
    // calling ResolveAsync and ResolveAllAsync back to back) doesn't rejoin the swarm twice.
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> _downloads = new(StringComparer.Ordinal);

    public ReleaseStreamResolver(
        IDebridService? debrid,
        ILogger<ReleaseStreamResolver> logger,
        ITorrentDownloader? downloader = null,
        IFileCache? files = null,
        DownloadConfig? download = null)
    {
        _debrid = debrid;
        _logger = logger;
        _downloader = downloader;
        _files = files;
        _download = download ?? new DownloadConfig();

        if (debrid is null)
            logger.LogInformation("No debrid service configured; the catalog will browse but nothing will play.");
    }

    private static readonly IReadOnlyDictionary<string, string> MimeByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        [".mkv"] = "video/x-matroska",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".avi"] = "video/x-msvideo",
        [".webm"] = "video/webm",

        // Audio
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".ogg"] = "audio/ogg",

        // Archive
        [".zip"] = "application/zip",
        [".rar"] = "application/vnd.rar",
        [".7z"] = "application/x-7z-compressed",

        // Document
        [".epub"] = "application/epub+zip",
        [".pdf"] = "application/pdf",
    };

    private const string DefaultMime = "application/octet-stream";

    /// <summary>The shape a debrid round trip settles into, before either public method
    /// turns it into its own return shape. <see cref="Outcome"/> is deliberately its own
    /// enum rather than reusing <see cref="DebridStatus"/> plus a "no debrid"/"threw"
    /// pair of booleans — collapsing "no debrid configured" and "the call threw" into
    /// one <see cref="ResolutionOutcome.Failed"/> case is exactly what both public
    /// methods already treated identically (null / empty list, no notice).</summary>
    private enum ResolutionOutcome { Failed, Pending, Resolved }

    private readonly record struct DebridOutcome(ResolutionOutcome Outcome, string? Notice, IReadOnlyList<DebridLink> Narrowed);

    /// <summary>
    /// The one path through the debrid call and status handling that both
    /// <see cref="ResolveAsync"/> and <see cref="ResolveAllAsync"/> build their return
    /// shape from — the null-debrid guard, the try/catch around the debrid call, and the
    /// <see cref="DebridStatus"/> switch (including the throw on an unhandled member) all
    /// live here exactly once, so a new <see cref="DebridStatus"/> member only needs
    /// handling in this one switch, not in two near-identical copies of it.
    /// </summary>
    private async Task<DebridOutcome> ResolveOutcomeAsync(TorrentResult release, MediaRequest request, CancellationToken cancellationToken)
    {
        if (_debrid is null)
            return new DebridOutcome(ResolutionOutcome.Failed, null, []);

        DebridResult result;
        try
        {
            result = await _debrid.ResolveAsync(release, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Debrid is a network call to someone else's paid service. It being down,
            // slow, or returning garbage must degrade to "nothing playable", never a 500.
            // A genuine caller cancellation (cancellationToken.IsCancellationRequested is true)
            // must still propagate, not be swallowed into a false-looking success.
            _logger.LogWarning(ex, "Debrid resolution threw for '{Title}'; treating as unplayable.", release.Title);
            return new DebridOutcome(ResolutionOutcome.Failed, null, []);
        }

        switch (result.Status)
        {
            case DebridStatus.Pending:
                return new DebridOutcome(ResolutionOutcome.Pending, result.Message ?? "Still caching; try again shortly.", []);

            case DebridStatus.Failed:
                _logger.LogInformation("Debrid resolution failed for '{Title}': {Message}", release.Title, result.Message);
                return new DebridOutcome(ResolutionOutcome.Failed, null, []);

            case DebridStatus.Resolved:
                return new DebridOutcome(ResolutionOutcome.Resolved, null, Narrow(request, result.Links));

            default:
                throw new InvalidOperationException($"Unhandled DebridStatus: {result.Status}");
        }
    }

    /// <summary>
    /// Resolve <paramref name="release"/> to a playable stream. Returns null when there is
    /// no debrid configured, resolution failed, or <paramref name="index"/> is past the end
    /// of the links debrid returned; returns a notice-only stream when the release is still
    /// caching; otherwise returns a stream with a URL and mime for the link at
    /// <paramref name="index"/>.
    /// </summary>
    public async Task<SourceStream?> ResolveAsync(TorrentResult release, MediaRequest request, int index, CancellationToken cancellationToken)
    {
        var outcome = await ResolveOutcomeAsync(release, request, cancellationToken);

        switch (outcome.Outcome)
        {
            case ResolutionOutcome.Failed:
                return null;

            case ResolutionOutcome.Pending:
                // A still-caching release has no per-file distinction from debrid, but a
                // successful self-download DOES — index walks the fetched file(s) exactly
                // like the Resolved branch below walks narrowed links. Falling back to the
                // notice (gate failed, or the download produced nothing) ignores index,
                // same as the no-fallback case always has.
                var downloaded = await TryFallbackDownloadAsync(release, request, cancellationToken);
                if (downloaded is null)
                    return SourceStream.FromNotice(outcome.Notice!);

                return index >= 0 && index < downloaded.Count ? downloaded[index] : null;

            default: // Resolved
                if (index < 0 || index >= outcome.Narrowed.Count)
                    return null;

                var link = outcome.Narrowed[index];
                return new SourceStream(link.Url.ToString(), MimeFor(link.FileName));
        }
    }

    /// <summary>
    /// Resolves every playable option for <paramref name="release"/> in a single debrid
    /// round trip, rather than one round trip per index. A caller that needs to walk
    /// files within a release before moving on to the next candidate release (see
    /// <see cref="Sources.MetadataBackedVideoSource"/>) uses this to learn how many
    /// options a release yields without resolving each one separately — resolving a
    /// release a second time just to ask "how many?" would double the round trips this
    /// method exists to avoid.
    /// <para>
    /// No configured debrid, a thrown exception, or a failed resolution all yield an
    /// empty list — nothing to walk into. A still-caching release yields a single
    /// notice option — so only n=0 of a walk sees the caching notice, unlike
    /// <see cref="ResolveAsync"/>, which ignores the index entirely for Pending and
    /// returns the notice regardless. A resolved release yields one option per narrowed
    /// link, in the same order <see cref="ResolveAsync"/> would index into.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SourceStream>> ResolveAllAsync(TorrentResult release, MediaRequest request, CancellationToken cancellationToken)
    {
        var outcome = await ResolveOutcomeAsync(release, request, cancellationToken);

        switch (outcome.Outcome)
        {
            case ResolutionOutcome.Failed:
                return [];

            case ResolutionOutcome.Pending:
                // A successful download replaces the single notice placeholder with the
                // fetched file(s) themselves — same fallback ResolveAsync uses above, so
                // the two paths can't drift on when the fallback applies.
                var downloaded = await TryFallbackDownloadAsync(release, request, cancellationToken);
                return downloaded is null ? [SourceStream.FromNotice(outcome.Notice!)] : downloaded;

            default: // Resolved
                return outcome.Narrowed.Select(link => new SourceStream(link.Url.ToString(), MimeFor(link.FileName))).ToList();
        }
    }

    /// <summary>
    /// Attempts to fetch a still-caching release directly instead of leaving the caller
    /// with only a notice. Returns null when any gate fails or the download produced
    /// nothing usable — both cases mean "behave exactly like today", i.e. the caller
    /// falls back to the ordinary <see cref="DebridResult"/> notice. The four gates, all
    /// of which must pass before a single byte moves:
    /// <list type="number">
    ///   <item><see cref="DownloadConfig.Enabled"/> is true.</item>
    ///   <item>A <see cref="ITorrentDownloader"/> (and somewhere to serve the result from)
    ///   was actually supplied.</item>
    ///   <item><paramref name="release"/>'s <see cref="TorrentResult.SizeBytes"/> is known
    ///   AND within <see cref="DownloadConfig.MaxSizeMB"/> — an unknown size never passes;
    ///   the whole point of the cap is to refuse an unbounded fetch, and "we don't know how
    ///   big it is" is exactly that.</item>
    ///   <item><paramref name="cancellationToken"/> is not already cancelled.</item>
    /// </list>
    /// </summary>
    private async Task<IReadOnlyList<SourceStream>?> TryFallbackDownloadAsync(
        TorrentResult release, MediaRequest request, CancellationToken cancellationToken)
    {
        if (!_download.Enabled)
            return null;

        var downloader = _downloader;
        var files = _files;
        if (downloader is null || files is null)
            return null;

        // A non-positive size is exactly as untrustworthy as no size at all — the cap
        // exists to refuse fetching something whose size we can't rely on, and 0 or a
        // negative value is not a real "small release", it's bad data.
        if (release.SizeBytes is not { } sizeBytes || sizeBytes <= 0 || sizeBytes > _download.MaxSizeMB * 1024L * 1024L)
            return null;

        if (cancellationToken.IsCancellationRequested)
            return null;

        IReadOnlyList<string> localPaths;
        try
        {
            localPaths = await GetOrDownloadAsync(downloader, files, release, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Same contract as the debrid call above: someone else's swarm/network being
            // slow, unreachable, or broken degrades to "nothing fetched", never a 500. A
            // genuine caller cancellation still propagates rather than being hidden as a
            // false-looking notice.
            _logger.LogWarning(ex, "Self-download of '{Title}' failed; falling back to the caching notice.", release.Title);
            return null;
        }

        if (localPaths.Count == 0)
            return null;

        var streams = new List<SourceStream>(localPaths.Count);
        foreach (var path in localPaths)
        {
            var built = await PublishAsync(files, release, request, path, cancellationToken).ConfigureAwait(false);
            if (built is not null)
                streams.Add(new SourceStream($"files/{built.ServedName}", built.ContentType));
        }

        if (streams.Count > 0)
        {
            // The engine is stopped and disposed once DownloadAsync returns, so the
            // .downloads working copy has no reseeding use — PublishAsync already moved
            // (not copied) every published file out of it, so this just clears whatever
            // the move left behind (unselected pieces, empty subdirectories, engine
            // scratch files). Best-effort: a lingering handle must not undo a download
            // that already succeeded and is already being served.
            RemoveDownloadDirectory(DownloadDirectory(files, release, request));
        }

        return streams.Count > 0 ? streams : null;
    }

    /// <summary>
    /// The memoized entry point for the actual swarm join — see the <see cref="_downloads"/>
    /// field doc for why this is a separate dictionary from <see cref="IFileCache"/>'s own.
    /// Follows the exact same eviction discipline as <see cref="FileCache.GetOrBuildAsync"/>:
    /// an empty result (no peers yet, a stalled swarm, a timeout — see
    /// <see cref="RunDownloadAsync"/>) or a thrown exception is a transient miss, not a
    /// permanent one, so it's evicted rather than left memoized for the rest of the
    /// process's life. Without this, one bad attempt at a release would disable the
    /// fallback for that exact release forever, since this resolver is a singleton.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetOrDownloadAsync(
        ITorrentDownloader downloader, IFileCache files, TorrentResult release, MediaRequest request, CancellationToken cancellationToken)
    {
        var key = ReleaseKey(release, request);
        var lazy = _downloads.GetOrAdd(key, _ => new Lazy<Task<IReadOnlyList<string>>>(
            () => RunDownloadAsync(downloader, files, release, request, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var result = await lazy.Value.ConfigureAwait(false);

            // Evict only AFTER the entry is certainly present, and only this exact Lazy —
            // matching on the (key, lazy) pair means a concurrent successful rebuild that
            // has already replaced this entry is never evicted by a late empty result.
            if (result.Count == 0)
                _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<string>>>>(key, lazy));

            return result;
        }
        catch
        {
            _downloads.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<string>>>>(key, lazy));
            throw;
        }
    }

    /// <summary>
    /// Actually joins the swarm, bounded by <see cref="DownloadConfig.TimeoutSeconds"/> and
    /// linked to <paramref name="cancellationToken"/> so a client disconnect stops it too. A
    /// timeout is treated the same as "no peers, gave up" — degrade to the notice, don't throw.
    /// </summary>
    private async Task<IReadOnlyList<string>> RunDownloadAsync(
        ITorrentDownloader downloader, IFileCache files, TorrentResult release, MediaRequest request, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_download.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var directory = DownloadDirectory(files, release, request);

        try
        {
            return await downloader.DownloadAsync(
                release, request, directory, progress: null,
                maxTotalBytes: _download.MaxSizeMB * 1024L * 1024L,
                cancellationToken: linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Self-download of '{Title}' timed out after {Seconds}s; falling back to the caching notice.",
                release.Title, _download.TimeoutSeconds);
            return [];
        }
    }

    /// <summary>
    /// Registers one already-downloaded local file with <see cref="IFileCache"/> so it
    /// becomes servable from <c>files/{name}</c> — <see cref="AddonEndpoints.MapFiles"/>
    /// serves straight off <see cref="IFileCache.Root"/> by plain file name, so the served
    /// name must be flat and must not collide across releases; it's prefixed with the same
    /// <see cref="ReleaseKey"/> the download itself was keyed by.
    /// <para>
    /// Moves rather than copies: the <c>.downloads</c> working copy is never read again
    /// (the engine is stopped and disposed once the download finishes, so there is no
    /// reseeding use for it), so leaving a second copy behind would just double the disk
    /// every fetched release occupies, forever.
    /// </para>
    /// </summary>
    private static async Task<BuiltFile?> PublishAsync(
        IFileCache files, TorrentResult release, MediaRequest request, string localPath, CancellationToken cancellationToken)
    {
        var servedName = $"{ReleaseKey(release, request)}-{Path.GetFileName(localPath)}";

        return await files.GetOrBuildAsync(servedName, (name, _) =>
        {
            var destination = Path.Combine(files.Root, name);
            if (!string.Equals(Path.GetFullPath(destination), Path.GetFullPath(localPath), StringComparison.OrdinalIgnoreCase))
                MoveOrCopy(localPath, destination);

            return Task.FromResult<BuiltFile?>(new BuiltFile(name, destination, MimeFor(name)));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>, falling back to
    /// copy-then-delete when a move isn't possible — chiefly a cross-volume move (the
    /// <c>.downloads</c> working directory and the served cache root are configurable
    /// independently and may live on different drives/mounts), which <see cref="File.Move"/>
    /// rejects rather than performing implicitly. Either way the source is gone afterward,
    /// so this never leaves the second, permanent copy the move exists to avoid.
    /// </summary>
    private static void MoveOrCopy(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }

    /// <summary>The <c>.downloads</c> working directory a release+request's fetch runs in —
    /// shared by <see cref="RunDownloadAsync"/> (which creates it) and
    /// <see cref="TryFallbackDownloadAsync"/> (which removes it once every file has been
    /// moved out).</summary>
    private static string DownloadDirectory(IFileCache files, TorrentResult release, MediaRequest request)
        => Path.Combine(files.Root, ".downloads", ReleaseKey(release, request));

    /// <summary>
    /// Best-effort removal of a now-empty (or nearly so) <c>.downloads</c> working
    /// directory after every file worth keeping has been moved out of it by
    /// <see cref="PublishAsync"/>. Deliberately narrow: this only ever touches the one
    /// working directory a single download used, never anything under the served cache
    /// root — a general eviction policy for served files is <see cref="IFileCache"/>'s
    /// own, pre-existing concern, not this method's.
    /// </summary>
    private static void RemoveDownloadDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A lingering file handle (e.g. an antivirus scan) must not undo an already-
            // successful, already-served download.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A short, stable, filesystem/URL-safe key identifying "this release, for this kind of
    /// request" — the unit both the download memoization and the served file names key off
    /// of. Prefers the info hash (present for almost every real release); a magnet or
    /// .torrent URL is a fine fallback identifier when it isn't, and the title is the last
    /// resort for a release with none of those (still deterministic, just less precise).
    /// <para>
    /// Beyond the request's own type and title, this must include every field that can
    /// change which file <see cref="MediaFileMatcher.Select{T}"/> — and, in turn,
    /// <see cref="MonoTorrentDownloader.SelectWantedFiles"/> — narrows a multi-file
    /// release down to. Otherwise two callers who differ only in, say, which episode of
    /// the same show they want would collide on the same memoized <see cref="_downloads"/>
    /// entry (and the same served-file name below) and the second caller would silently
    /// be handed the first caller's file. See <see cref="RequestDiscriminator"/>.
    /// </para>
    /// </summary>
    private static string ReleaseKey(TorrentResult release, MediaRequest request)
    {
        var identity = release.InfoHash ?? release.MagnetUri?.ToString() ?? release.DownloadUrl?.ToString() ?? release.Title;
        var seed = $"{identity}|{request.GetType().Name}|{request.Title}|{RequestDiscriminator(request)}";
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Every field, beyond <see cref="MediaRequest.Title"/>, that can change which file(s)
    /// <see cref="MediaFileMatcher.Select{T}"/> picks out of a multi-file release for this
    /// request's concrete type — see the switch in <see cref="MediaFileMatcher"/>'s private
    /// <c>Match</c>. Deliberately covers every field of each subtype that plausibly narrows
    /// a pack (not only the ones the matcher happens to read today, e.g.
    /// <see cref="TvRequest.AbsoluteEpisode"/> and <see cref="TvRequest.FullSeason"/> aren't
    /// read by <see cref="MediaFileMatcher"/> yet, but they identify a different unit of the
    /// same release and must not silently collide once something does start reading them).
    /// A new field on an existing subtype, or a wholly new subtype, needs a case added here
    /// whenever it can affect file selection — the same way it would need a case in
    /// <see cref="MediaFileMatcher"/>'s own switch.
    /// </summary>
    private static string RequestDiscriminator(MediaRequest request) => request switch
    {
        TvRequest tv => $"s={tv.Season}|e={tv.Episode}|abs={tv.AbsoluteEpisode}|full={tv.FullSeason}",
        MusicRequest m => $"artist={m.Artist}|album={m.Album}|track={m.Track}",
        BookRequest b => $"format={b.Format}",
        ComicRequest c => $"vol={c.Volume}|issue={c.Issue}|chap={c.Chapter}|format={c.Format}",
        GeneralRequest g => $"kind={g.Kind}|filetype={g.FileType}|filetypes={string.Join(',', g.FileTypes)}|filters={string.Join(',', g.FileFilters)}",
        _ => string.Empty,
    };

    // Single-extension whole-archive shapes, matched via Path.GetExtension. ".iso" is
    // deliberately absent: unlike zip/rar/7z/tar, a disc image is frequently the actual
    // deliverable a game or retro request wants (GeneralRequest.FileType="iso"), so
    // treating it as a "whole torrent, zipped" convenience link would deprioritize a
    // legitimate feature file instead of a wrapper around one.
    private static readonly string[] WholeArchiveExtensions = [".zip", ".zipx", ".rar", ".7z", ".tar", ".tgz"];

    // Multi-part RAR sets: classic ".r00".."r99"/"r999" volumes, and the modern
    // ".partN.rar" naming. Neither is a single recognizable Path.GetExtension() value.
    private static readonly Regex RarVolumePartExtension = new(@"^\.r\d{2,3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RarMultiPartName = new(@"\.part\d+\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Narrows debrid's raw per-file links down to what the request actually wants,
    /// before <paramref name="index"/> ever picks one. Two passes:
    /// <list type="number">
    ///   <item><see cref="MediaFileMatcher.SelectForRequest"/> — the episode/track/
    ///   book/comic picker Core already has, which narrows a season pack to one
    ///   episode, an album to one track, etc. Skipped entirely for
    ///   <see cref="UnknownMediaTypeRequest"/>: with no media type to key off of, the
    ///   general matcher would score a whole-torrent archive as the closest "title"
    ///   match to the release name (it has no extra tokens, unlike every real file)
    ///   and drop every real file — worse than doing nothing.</item>
    ///   <item><see cref="Deprioritize"/> — covers what step 1 leaves untouched:
    ///   movie, audiobook, non-specific TV/music requests (plus the skipped case
    ///   above), all of which fall through Core's matcher unchanged (including
    ///   whatever a debrid service prepended, e.g. TorBoxService's whole-torrent zip
    ///   at index 0, or an obvious sample file).</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<DebridLink> Narrow(MediaRequest request, IReadOnlyList<DebridLink> links)
    {
        var matched = request is UnknownMediaTypeRequest
            ? links
            : MediaFileMatcher.SelectForRequest(request, links);
        return Deprioritize(matched);
    }

    /// <summary>
    /// Pushes whole-torrent archive links (see TorBoxService.RequestLinksAsync's
    /// ".zip of everything" convenience link) and obvious sample files after every
    /// real media file, rather than removing them outright. Deliberately a reorder,
    /// not a filter: a request that genuinely wants an archive — a ROM or comic pack
    /// matched by extension — already has <see cref="MediaFileMatcher"/> narrow the
    /// candidates down to nothing BUT archives, so there is nothing to prefer over
    /// them and this is a no-op; same for a pack of nothing but sample-named files.
    /// What this rules out is index 0 handing back "everything, zipped" or a sample
    /// clip when a real file was sitting right next to it.
    /// </summary>
    private static IReadOnlyList<DebridLink> Deprioritize(IReadOnlyList<DebridLink> links)
    {
        if (links.Count <= 1)
            return links;

        var maxSize = links.Max(l => l.SizeBytes ?? 0);

        var archive = links.Where(l => IsWholeArchive(l.FileName)).ToList();
        var sample = links.Where(l => !IsWholeArchive(l.FileName) && IsLikelySample(l, maxSize)).ToList();
        var real = links.Where(l => !IsWholeArchive(l.FileName) && !IsLikelySample(l, maxSize)).ToList();

        // Nothing to demote (every link is a real file), or nothing real to prefer
        // (every link is an archive and/or a sample) — either way, leave order alone.
        if (real.Count == links.Count || real.Count == 0)
            return links;

        return [.. real, .. sample, .. archive];
    }

    private static bool IsWholeArchive(string fileName)
    {
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return true;
        if (RarMultiPartName.IsMatch(fileName))
            return true;

        var extension = Path.GetExtension(fileName);
        if (extension.Length == 0)
            return false;

        return WholeArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || RarVolumePartExtension.IsMatch(extension);
    }

    /// <summary>
    /// Whether <paramref name="link"/> looks like a promotional sample clip rather
    /// than the feature file: named by convention (<c>sample.*</c>, <c>*-sample.*</c>,
    /// or inside a <c>sample/</c> directory) AND smaller than the largest file in the
    /// set. Two signals, not just the name — a legitimate release can contain the
    /// word "sample" in its title without being one.
    /// </summary>
    private static bool IsLikelySample(DebridLink link, long maxSize)
        => HasSampleName(link.FileName) && (link.SizeBytes ?? 0) < maxSize;

    private static bool HasSampleName(string fileName)
    {
        var segments = fileName.Replace('\\', '/').Split('/');
        var stem = Path.GetFileNameWithoutExtension(segments[^1]);

        if (string.Equals(stem, "sample", StringComparison.OrdinalIgnoreCase))
            return true;
        if (stem.EndsWith("-sample", StringComparison.OrdinalIgnoreCase))
            return true;

        return segments.Take(segments.Length - 1)
            .Any(s => string.Equals(s, "sample", StringComparison.OrdinalIgnoreCase));
    }

    private static string MimeFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && MimeByExtension.TryGetValue(extension, out var mime)
            ? mime
            : DefaultMime;
    }
}
