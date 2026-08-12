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
    private readonly LibraryMetaCache _meta;
    private readonly ILogger _logger;

    // One server over the ROM roots: ResolveSafeFile/OpenAsync serve a game file, ResolveSafeDir gates a
    // platform folder (a strict subfolder of a root), IsContained backstops enumeration. ROMs have a
    // single root class, so unlike LocalLibrary one instance covers both file and directory resolution.
    private readonly SafeLocalFileServer _files;

    public RomLibrarySource(IReadOnlyList<string> roots, IResolverCache? cache, ILogger logger)
    {
        _roots = roots;
        _logger = logger;
        _files = new SafeLocalFileServer(roots, MimeFor);
        _meta = new LibraryMetaCache(cache);
    }

    // The cached per-ROM parse: gamelist fields + the located boxart absolute path (null if none/uncontained).
    // One entry serves both the platform-expand scan and the meta panel (same key: rom file + gamelist.xml).
    internal sealed record GameMeta(
        string? Name, string? Desc, int? Year,
        string? Developer, string? Publisher, string? Genre, string? Players, string? BoxartPath);

    // Computes a GameMeta for a ROM from its folder's gamelist index. Boxart is located gamelist-first
    // then by sibling discovery, and each candidate is BOTH existence- AND containment-checked through
    // the audited EncodeId→ResolveSafeFile round-trip (Decode→GetFullPath→File.Exists→IsContained) — so
    // a gamelist <image> of "../../secret.png" fails File.Exists-under-roots and yields null art, and
    // nothing outside a root is ever addressed. No new SafeLocalFileServer method is introduced.
    private GameMeta ComputeGameMeta(string romPath, GamelistIndex list)
    {
        var e = list.ForRom(romPath);
        var systemDir = Path.GetDirectoryName(romPath);

        string? art = null;
        // 1) gamelist <image>/<thumbnail>, resolved relative to the system folder, existence+containment checked.
        if (e?.ImageRelPath is { } rel && systemDir is not null)
        {
            var candidate = Path.Combine(systemDir, rel.Replace('\\', '/'));
            if (_files.ResolveSafeFile(SafeLocalFileServer.EncodeId(candidate)) is { } ok) art = ok;
        }
        // 2) sibling discovery, existence+containment checked.
        if (art is null && RomArtFinder.BoxartFor(romPath) is { } sib
            && _files.ResolveSafeFile(SafeLocalFileServer.EncodeId(sib)) is { } okSib) art = okSib;

        return new GameMeta(e?.Name, e?.Desc, e?.Year, e?.Developer, e?.Publisher, e?.Genre, e?.Players, art);
    }

    // The proxy URL a client fetches for a located boxart (mirrors LocalLibrary's PosterUrl): the id is
    // the boxart's own encoded absolute path, so OpenAsync re-checks containment before serving a byte.
    private string? BoxartUrl(string? boxartPath) => boxartPath is null
        ? null
        : $"proxy/{Key}/{SafeLocalFileServer.EncodeId(boxartPath)}/{Uri.EscapeDataString(Path.GetFileName(boxartPath))}";

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

    private static readonly EnumerationOptions TopLevelFiles = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    // Not-a-game files that commonly sit beside ROMs. A dotfile or one of these extensions is skipped;
    // everything else in a system folder is treated as a playable ROM (the folder is authoritative,
    // matching how the client accepts any non-junk file under a system folder).
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".xml", ".dat", ".md", ".ini", ".cfg", ".db", ".log",
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
        ".m3u", ".srm", ".state", ".sav", ".bak",
    };

    private static bool IsRom(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name[0] == '.') return false; // dotfiles
        return !JunkExtensions.Contains(Path.GetExtension(path));
    }

    // A platform id is a system folder (a real directory strictly inside a root, per ResolveSafeDir).
    // Its immediate non-junk files are the games. A game/file id has nothing to expand → empty.
    public async Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeDir(itemId) is not { } systemDir)
            return SourceCatalog.Empty("ROM Library");

        // Load the folder's gamelist.xml ONCE; each ROM's parse+art is memoized by (rom, gamelist mtime),
        // so the meta panel later reuses the same entry. The gamelist path is the cache's shared "nfoPath".
        var list = GamelistStore.Load(systemDir);
        var gamelistPath = GamelistStore.GamelistPath(systemDir);
        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var path in Directory.EnumerateFiles(systemDir, "*", TopLevelFiles))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRom(path)) continue;
            if (!_files.IsContained(path)) continue; // backstop
            if (items.Count >= MaxItems) { capped = true; break; }

            var meta = await _meta.GetOrComputeAsync<GameMeta>(path, gamelistPath,
                () => ComputeGameMeta(path, list), ct).ConfigureAwait(false);

            items.Add(new CatalogItem(
                Id: SafeLocalFileServer.EncodeId(path),
                Title: meta.Name ?? Path.GetFileNameWithoutExtension(path),
                Subtitle: Path.GetFileName(path),
                MediaType: "game",
                ThumbnailUrl: BoxartUrl(meta.BoxartPath),
                Expandable: false));
        }

        var title = RomSystems.Resolve(Path.GetFileName(systemDir))?.Title ?? Path.GetFileName(systemDir);
        // Sort by the human-visible title (gamelist name when present, else stem).
        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return new SourceCatalog(title, ordered, capped);
    }

    // The meta panel for a single game id: real name, description, boxart, and gamelist facts. A file id
    // (a game) resolves; a platform folder or foreign id yields null. Shares the DetailAsync cache entry.
    public async Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeFile(itemId) is not { } romPath)
            return null;

        var systemDir = Path.GetDirectoryName(romPath);
        var list = systemDir is null ? GamelistIndex.Empty : GamelistStore.Load(systemDir);
        var gamelistPath = systemDir is null ? null : GamelistStore.GamelistPath(systemDir);

        var meta = await _meta.GetOrComputeAsync<GameMeta>(romPath, gamelistPath,
            () => ComputeGameMeta(romPath, list), ct).ConfigureAwait(false);

        var facts = new List<MetaFact>(5);
        if (meta.Year is { } yr) facts.Add(new MetaFact("Year", yr.ToString()));
        if (meta.Genre is { } gn) facts.Add(new MetaFact("Genre", gn));
        if (meta.Players is { } pl) facts.Add(new MetaFact("Players", pl));
        if (meta.Developer is { } dv) facts.Add(new MetaFact("Developer", dv));
        if (meta.Publisher is { } pb) facts.Add(new MetaFact("Publisher", pb));

        return new SourceDetail(
            Title: meta.Name ?? Path.GetFileNameWithoutExtension(romPath),
            Overview: meta.Desc,
            ImageUrl: BoxartUrl(meta.BoxartPath),
            Facts: facts);
    }

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeFile(itemId) is not { } path)
            return Task.FromResult<SourceStream?>(null);

        // A relative addon path the host serves from the proxy route (OpenAsync). The filename — with
        // its extension — is in the path, so the client keeps the extension for the emulator.
        var url = $"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        return Task.FromResult<SourceStream?>(new SourceStream(url, MimeFor(path)));
    }

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
