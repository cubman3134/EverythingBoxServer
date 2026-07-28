using System.Text;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.SampleSource;

public sealed class LocalFolderConfig
{
    /// <summary>Absolute paths to scan. Nothing outside these is ever served.</summary>
    public List<string> Folders { get; set; } = [];
}

/// <summary>
/// A worked example of IMediaSource: scans configured folders, lists what it finds, and
/// relays bytes through the host's proxy route. Read this before writing your own.
/// </summary>
public sealed class LocalFolderSource(LocalFolderConfig config) : IMediaSource
{
    private static readonly Dictionary<string, string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = "video/x-matroska",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".flac"] = "audio/flac",
        [".opus"] = "audio/opus",
    };

    public string Key => "local";

    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } =
        [new CatalogDescriptor("files", "Local Files", "movie")];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        var items = new List<CatalogItem>();

        foreach (var folder in config.Folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                if (!MediaExtensions.ContainsKey(Path.GetExtension(path))) continue;

                var title = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                items.Add(new CatalogItem(
                    Id: EncodeId(path),
                    Title: title,
                    Subtitle: Describe(new FileInfo(path).Length),
                    MediaType: "movie"));
            }
        }

        return Task.FromResult(new SourceCatalog("Local Files", items));
    }

    // Files have nothing to expand into.
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("Local Files"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (ResolvePath(itemId) is not { } path)
            return Task.FromResult<SourceStream?>(null);

        var mime = MediaExtensions.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

        // A relative addon path: the host serves it from the proxy route below.
        var url = $"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        return Task.FromResult<SourceStream?>(new SourceStream(url, mime));
    }

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
    {
        if (ResolvePath(itemId) is not { } path)
            return Task.FromResult<ProxyResponse?>(null);

        var info = new FileInfo(path);
        var body = File.OpenRead(path);

        return Task.FromResult<ProxyResponse?>(new ProxyResponse(body, "application/octet-stream")
        {
            ContentLength = info.Length,
            AcceptRanges = "bytes",
        });
    }

    public static string EncodeId(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? DecodeId(string id)
    {
        try
        {
            var padded = id.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Decodes an id AND confirms it is inside a configured folder — an id
    /// arrives from the client, so it is never trusted on its own.</summary>
    private string? ResolvePath(string itemId)
    {
        if (DecodeId(itemId) is not { } decoded) return null;

        var full = Path.GetFullPath(decoded);
        if (!File.Exists(full)) return null;

        foreach (var folder in config.Folders)
        {
            var root = Path.GetFullPath(folder);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full;
        }
        return null;
    }

    private static string Describe(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB",
    };
}
