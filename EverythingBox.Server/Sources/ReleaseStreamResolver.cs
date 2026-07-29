using EverythingBox.Server.Abstractions;
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
                if (index < 0 || index >= result.Links.Count)
                    return null;

                var link = result.Links[index];
                return new SourceStream(link.Url.ToString(), MimeFor(link.FileName));

            default:
                throw new InvalidOperationException($"Unhandled DebridStatus: {result.Status}");
        }
    }

    private static string MimeFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && MimeByExtension.TryGetValue(extension, out var mime)
            ? mime
            : DefaultMime;
    }
}
