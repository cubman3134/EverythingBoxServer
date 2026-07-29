using System.Text.RegularExpressions;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Selection;
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
    /// notice option. A resolved release yields one option per narrowed link, in the
    /// same order <see cref="ResolveAsync"/> would index into.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SourceStream>> ResolveAllAsync(TorrentResult release, MediaRequest request, CancellationToken cancellationToken)
    {
        if (_debrid is null)
            return [];

        DebridResult result;
        try
        {
            result = await _debrid.ResolveAsync(release, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Debrid resolution threw for '{Title}'; treating as unplayable.", release.Title);
            return [];
        }

        switch (result.Status)
        {
            case DebridStatus.Pending:
                return [SourceStream.FromNotice(result.Message ?? "Still caching; try again shortly.")];

            case DebridStatus.Failed:
                _logger.LogInformation("Debrid resolution failed for '{Title}': {Message}", release.Title, result.Message);
                return [];

            case DebridStatus.Resolved:
                var narrowed = Narrow(request, result.Links);
                return narrowed.Select(link => new SourceStream(link.Url.ToString(), MimeFor(link.FileName))).ToList();

            default:
                throw new InvalidOperationException($"Unhandled DebridStatus: {result.Status}");
        }
    }

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
