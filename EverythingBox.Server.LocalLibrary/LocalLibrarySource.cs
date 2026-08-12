using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.LocalLibrary;

/// <summary>
/// Scans configured movie and series folders. Movie files become a "movies" catalog; each immediate
/// subfolder of a series root becomes an expandable show in a "series" catalog that DetailAsync
/// expands into its episodes. Bytes are relayed (with HTTP Range support) through the host's proxy
/// route. Every id that arrives from a client is decoded, real-resolved (following junctions/symlinks)
/// and confirmed to live inside a configured root before anything is served or expanded — an id is
/// never trusted on its own.
/// </summary>
public sealed class LocalLibrarySource : IMediaSource
{
    // A deliberate SUPERSET of the canonical video set (Abstractions' MediaFileMatcher.VideoExtensions
    // = .mkv .mp4 .avi .m4v .ts .mov .wmv): it also lists .webm .flv .mpg .mpeg, chosen to match the
    // serving MIME map below so listing and serving stay consistent for a local library. Kept local
    // because that member is not accessible to a plugin assembly and the plugin must not change the
    // shared contract. Do NOT "sync" the two lists toward the canonical set — the extra entries are
    // intentional; the MIME map, not MediaFileMatcher, is the peer to keep this in step with.
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm", ".wmv", ".flv", ".ts", ".mpg", ".mpeg",
    };

    private const int MaxItems = 5000;

    private readonly IReadOnlyList<string> _movieRoots;
    private readonly IReadOnlyList<string> _seriesRoots;
    private readonly LibraryMetaCache _meta;
    private readonly ILogger _logger;

    // Serving + file resolution + the enumeration containment backstop, over ALL configured roots.
    private readonly SafeLocalFileServer _files;
    // Directory resolution scoped to SERIES roots: a show folder must be under a series root.
    private readonly SafeLocalFileServer _seriesDirs;

    public LocalLibrarySource(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, IResolverCache? cache, ILogger logger)
    {
        _movieRoots = movieRoots;
        _seriesRoots = seriesRoots;
        _meta = new LibraryMetaCache(cache);
        _logger = logger;
        _files = new SafeLocalFileServer([.. _movieRoots, .. _seriesRoots], MimeFor);
        _seriesDirs = new SafeLocalFileServer(_seriesRoots, MimeFor);
    }

    public string Key => "locallib";

    // A fresh checkout with no configured roots serves nothing: a shelf appears only for a root kind
    // that is actually configured.
    public IReadOnlyList<CatalogDescriptor> Catalogs
    {
        get
        {
            var list = new List<CatalogDescriptor>(2);
            if (_movieRoots.Count > 0) list.Add(new CatalogDescriptor("movies", "Movies", "movie"));
            if (_seriesRoots.Count > 0) list.Add(new CatalogDescriptor("series", "Series", "series"));
            return list;
        }
    }

    // ReparsePoint here means a junction/symlink is never descended into, so it can't leak files
    // from outside a configured folder or loop back on an ancestor; it doesn't apply to the root
    // passed to EnumerateFiles, so a configured folder that is itself a reparse point still works.
    // IgnoreInaccessible skips an unreadable subdirectory instead of aborting the whole walk. We set
    // AttributesToSkip to ONLY ReparsePoint, overriding the framework default (Hidden | System): a
    // media server silently hiding a file the owner incidentally marked hidden (from a download,
    // NAS, or sync tool) is worse than listing it.
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    public async Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => catalogId switch
        {
            "movies" => await ScanMovies(query, ct),
            "series" => await ListShows(query, ct),
            _ => SourceCatalog.Empty("Local Library"),
        };

    private async Task<SourceCatalog> ScanMovies(string? query, CancellationToken ct)
    {
        var parser = new DefaultReleaseParser();
        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var folder in _movieRoots)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;

            foreach (var path in Directory.EnumerateFiles(folder, "*", WalkOptions))
            {
                ct.ThrowIfCancellationRequested();

                if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;

                // The enumerator prevents junction escapes during listing via AttributesToSkip.
                // This check is a deliberate backstop: if WalkOptions is ever loosened, both
                // SearchAsync and ResolveSafeFile enforce the same containment discipline via the
                // one shared implementation, so they can never quietly diverge.
                if (!_files.IsContained(path)) continue;

                var nfoPath = MovieNfo(path);
                var meta = await _meta.GetOrComputeAsync<ItemMeta>(path, nfoPath,
                    () =>
                    {
                        var n = nfoPath is null ? null : NfoReader.TryRead(nfoPath);
                        return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(path));
                    }, ct).ConfigureAwait(false);

                var title = meta.NfoTitle is { } nt
                    ? (meta.Year is { } ny ? $"{nt} ({ny})" : nt)
                    : TitleFor(parser, path);

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(
                    Id: SafeLocalFileServer.EncodeId(path),
                    Title: title,
                    Subtitle: Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
                    Kind: MediaTypeNames.Movie,
                    ThumbnailUrl: PosterUrl(meta.PosterPath),
                    Expandable: false));
            }

            if (capped) break;
        }

        var ordered = items
            .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SourceCatalog("Movies", ordered, capped);
    }

    private static readonly EnumerationOptions TopLevelDirs = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private async Task<SourceCatalog> ListShows(string? query, CancellationToken ct)
    {
        var parser = new DefaultReleaseParser();
        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var root in _seriesRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", TopLevelDirs))
            {
                ct.ThrowIfCancellationRequested();
                if (!_files.IsContained(dir)) continue;

                var tvshow = Path.Combine(dir, "tvshow.nfo");
                var nfoPath = File.Exists(tvshow) ? tvshow : null;
                var meta = await _meta.GetOrComputeAsync<ItemMeta>(dir, nfoPath,
                    () =>
                    {
                        var n = nfoPath is null ? null : NfoReader.TryRead(nfoPath);
                        return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(dir));
                    }, ct).ConfigureAwait(false);

                var title = meta.NfoTitle ?? ShowTitle(parser, dir);

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(Id: SafeLocalFileServer.EncodeId(dir), Title: title, Subtitle: string.Empty,
                    Kind: MediaTypeNames.Series, ThumbnailUrl: PosterUrl(meta.PosterPath), Expandable: true));
            }
            if (capped) break;
        }

        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return new SourceCatalog("Series", ordered, capped);
    }

    // A movie/episode file id has nothing to expand; only a series folder id (a real directory inside
    // a series root, per ResolveSafeDir) expands into that show's episodes.
    public async Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_seriesDirs.ResolveSafeDir(itemId) is not { } showDir)
            return SourceCatalog.Empty("Local Library");

        var parser = new DefaultReleaseParser();
        var episodes = new List<(int Season, int Episode, CatalogItem Item)>();

        foreach (var path in Directory.EnumerateFiles(showDir, "*", WalkOptions))
        {
            ct.ThrowIfCancellationRequested();
            if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;
            if (!_files.IsContained(path)) continue;

            var info = parser.Parse(Path.GetFileNameWithoutExtension(path), MediaType.Tv);
            if (info.Season is not { } season || info.Episodes.Count == 0) continue;
            var episode = info.Episodes[0];

            // Same MaxItems bound the catalog listings use, so one pathological folder can't build a
            // giant list. No HasMore flag is needed — the detail view isn't paged like search.
            if (episodes.Count >= MaxItems) break;

            var epNfoPath = Path.ChangeExtension(path, ".nfo");
            var existsEpNfo = File.Exists(epNfoPath) ? epNfoPath : null;
            var meta = await _meta.GetOrComputeAsync<ItemMeta>(path, existsEpNfo,
                () =>
                {
                    var n = existsEpNfo is null ? null : NfoReader.TryRead(existsEpNfo);
                    // Populate the poster too: DetailAsync and MetaAsync share this cache entry
                    // (same key: episode file + sidecar), so the entry must be complete or the meta
                    // panel inherits a null poster after a list browse fills it first.
                    return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(path));
                }, ct).ConfigureAwait(false);
            var epTitle = $"S{season:D2}E{episode:D2}" + (meta.NfoTitle is { } et ? $" - {et}" : "");

            episodes.Add((season, episode, new CatalogItem(
                Id: SafeLocalFileServer.EncodeId(path),
                Title: epTitle,
                Subtitle: Path.GetFileName(path),
                Kind: MediaTypeNames.Series,
                Expandable: false)));
        }

        var ordered = episodes
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .Select(e => e.Item)
            .ToList();

        var title = ShowTitle(parser, showDir);
        return new SourceCatalog(title, ordered);
    }

    public async Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        // A file id (movie/episode) → its own .nfo; a series folder id → tvshow.nfo.
        if (_files.ResolveSafeFile(itemId) is { } file)
        {
            var nfoPath = MovieNfo(file);
            var meta = await _meta.GetOrComputeAsync<ItemMeta>(file, nfoPath,
                () =>
                {
                    var n = nfoPath is null ? null : NfoReader.TryRead(nfoPath);
                    return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(file));
                }, ct).ConfigureAwait(false);

            var title = meta.NfoTitle ?? TitleFor(new DefaultReleaseParser(), file);
            var facts = meta.Year is { } y ? new[] { new MetaFact("Year", y.ToString()) } : [];
            return new SourceDetail(
                Title: title, Overview: meta.Plot, ImageUrl: PosterUrl(meta.PosterPath), Facts: facts);
        }
        if (_seriesDirs.ResolveSafeDir(itemId) is { } dir)
        {
            var tvshow = Path.Combine(dir, "tvshow.nfo");
            var nfoPath = File.Exists(tvshow) ? tvshow : null;
            var meta = await _meta.GetOrComputeAsync<ItemMeta>(dir, nfoPath,
                () =>
                {
                    var n = nfoPath is null ? null : NfoReader.TryRead(nfoPath);
                    return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(dir));
                }, ct).ConfigureAwait(false);

            var title = meta.NfoTitle ?? ShowTitle(new DefaultReleaseParser(), dir);
            return new SourceDetail(
                Title: title, Overview: meta.Plot, ImageUrl: PosterUrl(meta.PosterPath));
        }
        return null;
    }

    // The .nfo for a media FILE: "<stem>.nfo" sidecar, else "movie.nfo" in the same folder.
    private static string? MovieNfo(string file)
    {
        var sidecar = Path.ChangeExtension(file, ".nfo");
        if (sidecar is not null && File.Exists(sidecar)) return sidecar;
        var dir = Path.GetDirectoryName(file);
        var movieNfo = dir is null ? null : Path.Combine(dir, "movie.nfo");
        return movieNfo is not null && File.Exists(movieNfo) ? movieNfo : null;
    }

    private string? PosterUrl(string? posterPath) => posterPath is null
        ? null
        : $"proxy/{Key}/{SafeLocalFileServer.EncodeId(posterPath)}/{Uri.EscapeDataString(Path.GetFileName(posterPath))}";

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeFile(itemId) is not { } path)
            return Task.FromResult<SourceStream?>(null);

        // A relative addon path: the host serves it from the proxy route (OpenAsync).
        var url = $"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        return Task.FromResult<SourceStream?>(new SourceStream(url, MimeFor(path)));
    }

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => _files.OpenAsync(itemId, rangeHeader, ct);

    // A show's display title: the parsed NormalizedTitle of the folder name, falling back to the raw
    // folder name. Shared by ListShows and DetailAsync so a folder like "Breaking.Show.2008" reads the
    // same in the listing and the expanded view.
    private static string ShowTitle(DefaultReleaseParser parser, string dirPath)
    {
        var name = Path.GetFileName(dirPath);
        var parsed = parser.Parse(name, MediaType.Tv).NormalizedTitle;
        return string.IsNullOrWhiteSpace(parsed) ? name : parsed;
    }

    private static string TitleFor(DefaultReleaseParser parser, string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var info = parser.Parse(stem, MediaType.Movie);
        var title = string.IsNullOrWhiteSpace(info.NormalizedTitle) ? stem : info.NormalizedTitle;
        return info.Year is { } year ? $"{title} ({year})" : title;
    }

    // A small extension -> MIME map, case-insensitive on the extension. Images are served too:
    // a located poster is opened through the same proxy/Range path as a media file.
    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mkv" => "video/x-matroska",
        ".mp4" or ".m4v" => "video/mp4",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".wmv" => "video/x-ms-wmv",
        ".flv" => "video/x-flv",
        ".ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

}
