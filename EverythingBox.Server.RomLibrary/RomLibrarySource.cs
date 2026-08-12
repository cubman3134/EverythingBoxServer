using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.RomLibrary;

/// <summary>
/// Scans configured ROM roots. Each immediate subfolder of a root is a console: it becomes an
/// expandable "platform" item in a single "games" catalog, titled with a recognizable console name so
/// the client picks the right emulator (the platform title is the ONLY systemHint channel). DetailAsync
/// expands a platform into its ROM files as "game" items; bytes are relayed with HTTP Range through the
/// host proxy route. Every id is decoded, real-resolved and confirmed inside a root before anything is
/// served or expanded — via the shared SafeLocalFileServer; an id is never trusted on its own.
/// </summary>
public sealed class RomLibrarySource : IMediaSource
{
    private const int MaxItems = 5000;

    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger _logger;

    // One server over the ROM roots: ResolveSafeFile/OpenAsync serve a game file, ResolveSafeDir gates a
    // platform folder (a strict subfolder of a root), IsContained backstops enumeration. ROMs have a
    // single root class, so unlike LocalLibrary one instance covers both file and directory resolution.
    private readonly SafeLocalFileServer _files;

    public RomLibrarySource(IReadOnlyList<string> roots, ILogger logger)
    {
        _roots = roots;
        _logger = logger;
        _files = new SafeLocalFileServer(roots, MimeFor);
    }

    public string Key => "romlib";

    // A fresh checkout with no configured roots serves nothing.
    public IReadOnlyList<CatalogDescriptor> Catalogs
        => _roots.Count > 0 ? [new CatalogDescriptor("games", "Games", "game")] : [];

    // Immediate subfolders only; a junction/symlink subfolder is skipped (never descended) so it can't
    // leak a console folder from outside a root. IgnoreInaccessible skips an unreadable dir.
    private static readonly EnumerationOptions TopLevelDirs = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        if (catalogId != "games") return Task.FromResult(SourceCatalog.Empty("ROM Library"));

        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var root in _roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", TopLevelDirs))
            {
                ct.ThrowIfCancellationRequested();
                if (!_files.IsContained(dir)) continue; // backstop; TopLevelDirs already blocks junctions

                var folderName = Path.GetFileName(dir);
                var title = RomSystems.Resolve(folderName)?.Title ?? folderName;

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(
                    Id: SafeLocalFileServer.EncodeId(dir),
                    Title: title,
                    Subtitle: string.Empty,
                    MediaType: "platform",
                    ThumbnailUrl: null,
                    Expandable: true));
            }
            if (capped) break;
        }

        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult(new SourceCatalog("Games", ordered, capped));
    }

    // Filled in Task 2.
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("ROM Library"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => _files.OpenAsync(itemId, rangeHeader, ct);

    // ext -> "application/x-<ext>" so the client can recover the ROM extension from the mime when the
    // url path lacks it; the proxy url DOES carry the filename, so this is a belt-and-suspenders map.
    // Art extensions map to image types (unused until Inc 3's boxart).
    private static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "" => "application/octet-stream",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            _ => $"application/x-{ext}",
        };
    }
}
