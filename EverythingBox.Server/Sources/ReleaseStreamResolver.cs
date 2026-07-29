using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Selection;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Sources;

/// <summary>
/// Turns a chosen <see cref="TorrentResult"/> into something the client can play, via
/// whatever <see cref="IDebridService"/> the host was configured with. There is
/// deliberately no self-download fallback here — an uncached release comes back as a
/// notice, not a download queued on the host's own hardware; that belongs to a later
/// plan.
/// </summary>
public sealed class ReleaseStreamResolver
{
    private readonly IDebridService? _debrid;
    private readonly ILogger<ReleaseStreamResolver> _logger;

    public ReleaseStreamResolver(IDebridService? debrid, ILogger<ReleaseStreamResolver> logger)
    {
        _debrid = debrid;
        _logger = logger;

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

    /// <summary>
    /// Resolve <paramref name="release"/> to a playable stream. Returns null when there is
    /// no debrid configured, resolution failed, or <paramref name="index"/> is past the end
    /// of the links debrid returned; returns a notice-only stream when the release is still
    /// caching; otherwise returns a stream with a URL and mime for the link at
    /// <paramref name="index"/>.
    /// </summary>
    public async Task<SourceStream?> ResolveAsync(TorrentResult release, MediaRequest request, int index, CancellationToken cancellationToken)
    {
        if (_debrid is null)
            return null;

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
            return null;
        }

        switch (result.Status)
        {
            case DebridStatus.Pending:
                return SourceStream.FromNotice(result.Message ?? "Still caching; try again shortly.");

            case DebridStatus.Failed:
                _logger.LogInformation("Debrid resolution failed for '{Title}': {Message}", release.Title, result.Message);
                return null;

            case DebridStatus.Resolved:
                var narrowed = Narrow(request, result.Links);
                if (index < 0 || index >= narrowed.Count)
                    return null;

                var link = narrowed[index];
                return new SourceStream(link.Url.ToString(), MimeFor(link.FileName));

            default:
                throw new InvalidOperationException($"Unhandled DebridStatus: {result.Status}");
        }
    }

    private static readonly string[] WholeArchiveExtensions = [".zip", ".rar", ".7z"];

    /// <summary>
    /// Narrows debrid's raw per-file links down to what the request actually wants,
    /// before <paramref name="index"/> ever picks one. Two passes:
    /// <list type="number">
    ///   <item><see cref="MediaFileMatcher.SelectForRequest"/> — the episode/track/
    ///   book/comic picker Core already has, which narrows a season pack to one
    ///   episode, an album to one track, etc.</item>
    ///   <item><see cref="DeprioritizeWholeArchives"/> — covers what step 1 leaves
    ///   untouched: movie, audiobook, non-specific TV/music requests, all of which
    ///   fall through Core's matcher unchanged (including whatever a debrid service
    ///   prepended, e.g. TorBoxService's whole-torrent zip at index 0).</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<DebridLink> Narrow(MediaRequest request, IReadOnlyList<DebridLink> links)
    {
        var matched = MediaFileMatcher.SelectForRequest(request, links);
        return DeprioritizeWholeArchives(matched);
    }

    /// <summary>
    /// Pushes whole-torrent archive links (see TorBoxService.RequestLinksAsync's
    /// ".zip of everything" convenience link) after every real media file, rather
    /// than removing them outright. Deliberately a reorder, not a filter: a request
    /// that genuinely wants an archive — a ROM or comic pack matched by extension —
    /// already has <see cref="MediaFileMatcher"/> narrow the candidates down to
    /// nothing BUT archives, so there is nothing non-archive to prefer and this is a
    /// no-op; the archive still comes back, just still at its earlier position. What
    /// this rules out is the one thing that must never happen: index 0 handing back
    /// "everything, zipped" when a real file was sitting right next to it.
    /// </summary>
    private static IReadOnlyList<DebridLink> DeprioritizeWholeArchives(IReadOnlyList<DebridLink> links)
    {
        if (links.Count <= 1)
            return links;

        var nonArchive = links.Where(l => !IsWholeArchive(l.FileName)).ToList();
        if (nonArchive.Count == 0 || nonArchive.Count == links.Count)
            return links;

        var archive = links.Where(l => IsWholeArchive(l.FileName)).ToList();
        return [.. nonArchive, .. archive];
    }

    private static bool IsWholeArchive(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && WholeArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string MimeFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && MimeByExtension.TryGetValue(extension, out var mime)
            ? mime
            : DefaultMime;
    }
}
