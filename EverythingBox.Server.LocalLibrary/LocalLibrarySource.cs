using System.Text;
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
    private readonly ILogger _logger;

    public LocalLibrarySource(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, ILogger logger)
    {
        _movieRoots = movieRoots;
        _seriesRoots = seriesRoots;
        _logger = logger;
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

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        return catalogId switch
        {
            "movies" => Task.FromResult(ScanMovies(query, ct)),
            "series" => Task.FromResult(ListShows(query, ct)),
            _ => Task.FromResult(SourceCatalog.Empty("Local Library")),
        };
    }

    private SourceCatalog ScanMovies(string? query, CancellationToken ct)
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
                // SearchAsync and ResolveSafePath enforce the same containment discipline via the
                // one shared implementation, so they can never quietly diverge.
                if (!IsContained(path)) continue;

                var title = TitleFor(parser, path);

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(
                    Id: EncodeId(path),
                    Title: title,
                    Subtitle: Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
                    MediaType: "movie",
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

    private SourceCatalog ListShows(string? query, CancellationToken ct)
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
                if (!IsContained(dir)) continue;

                var title = ShowTitle(parser, dir);

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(Id: EncodeId(dir), Title: title, Subtitle: string.Empty,
                    MediaType: "series", Expandable: true));
            }
            if (capped) break;
        }

        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return new SourceCatalog("Series", ordered, capped);
    }

    // A movie/episode file id has nothing to expand; only a series folder id (a real directory inside
    // a series root, per ResolveSafeDir) expands into that show's episodes.
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (ResolveSafeDir(itemId) is not { } showDir)
            return Task.FromResult(SourceCatalog.Empty("Local Library"));

        var parser = new DefaultReleaseParser();
        var episodes = new List<(int Season, int Episode, CatalogItem Item)>();

        foreach (var path in Directory.EnumerateFiles(showDir, "*", WalkOptions))
        {
            ct.ThrowIfCancellationRequested();
            if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;
            if (!IsContained(path)) continue;

            var info = parser.Parse(Path.GetFileNameWithoutExtension(path), MediaType.Tv);
            if (info.Season is not { } season || info.Episodes.Count == 0) continue;
            var episode = info.Episodes[0];

            // Same MaxItems bound the catalog listings use, so one pathological folder can't build a
            // giant list. No HasMore flag is needed — the detail view isn't paged like search.
            if (episodes.Count >= MaxItems) break;

            episodes.Add((season, episode, new CatalogItem(
                Id: EncodeId(path),
                Title: $"S{season:D2}E{episode:D2}",
                Subtitle: Path.GetFileName(path),
                MediaType: "series",
                Expandable: false)));
        }

        var ordered = episodes
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .Select(e => e.Item)
            .ToList();

        var title = ShowTitle(parser, showDir);
        return Task.FromResult(new SourceCatalog(title, ordered));
    }

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (ResolveSafePath(itemId) is not { } path)
            return Task.FromResult<SourceStream?>(null);

        // A relative addon path: the host serves it from the proxy route (OpenAsync).
        var url = $"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        return Task.FromResult<SourceStream?>(new SourceStream(url, MimeFor(path)));
    }

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
    {
        var path = ResolveSafePath(itemId);
        if (path is null) return Task.FromResult<ProxyResponse?>(null);

        var info = new FileInfo(path);
        if (!info.Exists) return Task.FromResult<ProxyResponse?>(null);

        var total = info.Length;
        var mime = MimeFor(path);
        var result = RangeRequest.Parse(rangeHeader, total);

        if (result.Kind == RangeKind.Unsatisfiable)
            return Task.FromResult<ProxyResponse?>(new ProxyResponse(Stream.Null, mime)
            {
                StatusCode = 416, AcceptRanges = "bytes", ContentRange = $"bytes */{total}", ContentLength = 0,
            });

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);

        if (result.Kind == RangeKind.Partial)
        {
            file.Seek(result.Start, SeekOrigin.Begin);
            // BoundedReadStream owns and disposes `file`; ProxyResponse disposes the BoundedReadStream.
            return Task.FromResult<ProxyResponse?>(new ProxyResponse(new BoundedReadStream(file, result.Length), mime)
            {
                StatusCode = 206, ContentLength = result.Length, AcceptRanges = "bytes",
                ContentRange = $"bytes {result.Start}-{result.Start + result.Length - 1}/{total}",
            });
        }

        // Full: the FileStream IS the body — the host's ProxyResponse.DisposeAsync disposes it.
        return Task.FromResult<ProxyResponse?>(new ProxyResponse(file, mime)
        {
            StatusCode = 200, ContentLength = total, AcceptRanges = "bytes",
        });
    }

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

    internal static string EncodeId(string absolutePath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(absolutePath)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string? TryDecodeId(string id)
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

    // NTFS is case-insensitive-but-preserving; most non-Windows filesystems are case-sensitive,
    // where two lexically-different-case paths are unrelated files. Pick the comparison the real
    // filesystem uses rather than hardcoding one.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Decodes an id AND confirms it resolves to a real file inside a configured root — an
    /// id arrives from the client, so it is never trusted on its own. Returns null for any bad id.
    /// Under a delete race — the file removed after the File.Exists gate — the real-resolve step (or a
    /// later stream open) may instead surface an I/O exception rather than null; that is harmless, as
    /// the host maps any OpenAsync throw to a 404, and it matches the reviewed LocalFolderSource.</summary>
    internal string? ResolveSafePath(string itemId)
    {
        if (TryDecodeId(itemId) is not { } decoded) return null;

        string full;
        try
        {
            // A string that decodes cleanly from base64 is not necessarily a path GetFullPath will
            // accept — an id is client-controlled all the way down. Catch what GetFullPath documents
            // itself as throwing, not Exception broadly, so an unrelated bug still surfaces.
            full = Path.GetFullPath(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(full)) return null;

        return IsContained(full) ? full : null;
    }

    /// <summary>Decodes an id and confirms it is a real directory strictly UNDER a configured SERIES
    /// root — gating DetailAsync so an arbitrary or foreign folder id can never be enumerated. A show
    /// is always a strict subfolder of a root, so the root itself is rejected. Null for any bad id, a
    /// file, a series root, or a directory outside the series roots.</summary>
    internal string? ResolveSafeDir(string itemId)
    {
        if (TryDecodeId(itemId) is not { } decoded) return null;

        string full;
        try { full = Path.GetFullPath(decoded); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }

        if (!Directory.Exists(full)) return null;

        var resolved = ResolveReal(full);
        foreach (var root in _seriesRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string r;
            try { r = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { continue; }
            var resolvedRoot = ResolveReal(r);
            // A show is always a strict SUBFOLDER of a series root; the root itself is never a show.
            // Accept only a directory strictly UNDER a root — an id forged for the root must not
            // flatten the whole root into episodes. (FILE serving via ResolveSafePath/IsContained is
            // unchanged: a file directly in a root must still serve.)
            if (resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, PathComparison))
                return resolved;
        }
        return null;
    }

    /// <summary>True if <paramref name="full"/> — a path already confirmed to exist — actually
    /// resolves, after following every reparse point in its ancestor chain, to somewhere inside a
    /// configured root (also resolved the same way). Shared by ResolveSafePath (a decoded client id)
    /// and SearchAsync (every enumerated path) so opening and listing can never diverge.</summary>
    private bool IsContained(string full)
    {
        // Path.GetFullPath only does LEXICAL normalization (collapses "..", ".", relative segments);
        // it does NOT follow reparse points. A junction or symlink planted inside a configured root
        // can point anywhere, and File.Exists/File.OpenRead/EnumerateFiles all transparently follow
        // it — so the file actually served (or listed) can live entirely outside every configured
        // root even though the lexical path looks contained. Resolve the candidate to where it
        // really is — walking every directory in its ancestor chain, since the leaf OR any directory
        // above it can be the link — and compare THAT against the roots (also resolved, so a
        // legitimately-linked root still works).
        var resolvedFull = ResolveReal(full);

        foreach (var folder in _movieRoots.Concat(_seriesRoots))
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;

            string root;
            try
            {
                // A configured folder is operator-entered config, not guaranteed to be a path
                // GetFullPath accepts. Trim trailing separators before GetFullPath re-adds its own,
                // or a folder configured with one ("D:\Media\Movies\") doubles up and nothing inside
                // it ever matches. Skip an unusable entry rather than crashing every other lookup.
                root = Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
            {
                continue;
            }

            var resolvedRoot = ResolveReal(root);

            if (resolvedFull.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, PathComparison) ||
                resolvedFull.Equals(resolvedRoot, PathComparison))
                return true;
        }
        return false;
    }

    /// <summary>Resolves a path to where it actually points on disk, the way the filesystem would
    /// when opening it: walks the path one segment at a time from the root, resolving every directory
    /// reparse point (junction/symlink) it passes through — not just the leaf — since the escape that
    /// matters is a junction planted partway down the chain, not only at the end. Each resolved
    /// target is itself already fully resolved (ResolveLinkTarget follows a chain to its final
    /// destination). A path with no links anywhere resolves to itself.</summary>
    private static string ResolveReal(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var segments = path[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var resolved = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = Path.Combine(resolved, segments[i]);
            var isLeaf = i == segments.Length - 1;

            // Intermediate segments are always directories. The leaf may be a file (the common case,
            // since callers already confirmed File.Exists) or itself a directory reparse point.
            var target = isLeaf
                ? File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName
                  ?? Directory.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName
                : Directory.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName;

            resolved = target ?? candidate;
        }

        return resolved;
    }
}
