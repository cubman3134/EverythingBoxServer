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

    // ReparsePoint here means a junction/symlink is never descended into, so it can't leak files
    // from outside a configured folder or loop back on an ancestor; it doesn't apply to the root
    // passed to EnumerateFiles, so a configured folder that is itself a reparse point still works.
    // IgnoreInaccessible skips an unreadable subdirectory instead of aborting the whole walk.
    // We explicitly set AttributesToSkip to ONLY ReparsePoint, overriding the framework default
    // (which includes Hidden | System). A media server hiding a user's file with no indication is
    // worse than listing one they incidentally marked hidden — files can pick up that attribute
    // from a download, NAS, or sync tool without the owner's intent.
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        var items = new List<CatalogItem>();

        foreach (var folder in config.Folders)
        {
            if (!Directory.Exists(folder)) continue;

            foreach (var path in Directory.EnumerateFiles(folder, "*", WalkOptions))
            {
                ct.ThrowIfCancellationRequested();

                if (!MediaExtensions.ContainsKey(Path.GetExtension(path))) continue;

                // The enumerator prevents junction escapes during listing via AttributesToSkip.
                // This check is a deliberate backstop: if WalkOptions is ever loosened, or
                // containment logic changes, both SearchAsync and ResolvePath enforce the same
                // discipline — sharing one implementation so they can never quietly diverge.
                if (!IsContained(path)) continue;

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

    // NTFS is case-insensitive-but-preserving; most non-Windows filesystems are case-sensitive,
    // where two lexically-different-case paths are unrelated files. Pick the comparison the real
    // filesystem uses rather than hardcoding one.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Decodes an id AND confirms it is inside a configured folder — an id
    /// arrives from the client, so it is never trusted on its own.</summary>
    private string? ResolvePath(string itemId)
    {
        if (DecodeId(itemId) is not { } decoded) return null;

        string full;
        try
        {
            // A string that decodes cleanly from base64 is not necessarily a path
            // Path.GetFullPath will accept — an id is client-controlled all the way down, and
            // this class only ever returns null for a bad one, never throws. Two demonstrated
            // cases: "" ("The path is empty"), and a string with an embedded NUL ("Null
            // character in path"). Catch what GetFullPath documents itself as throwing, not
            // Exception broadly, so an unrelated bug still surfaces instead of being swallowed.
            full = Path.GetFullPath(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(full)) return null;

        return IsContained(full) ? full : null;
    }

    /// <summary>True if `full` — a path already confirmed to exist — actually resolves, after
    /// following every reparse point in its ancestor chain, to somewhere inside a configured
    /// folder (also resolved the same way). Shared by ResolvePath (a decoded client id, before
    /// serving it) and SearchAsync (every enumerated path, before listing it) — opening and
    /// listing need the identical containment discipline, and a second, slightly different copy
    /// of this check is exactly how the listing side previously fell behind.</summary>
    private bool IsContained(string full)
    {
        // Path.GetFullPath only does LEXICAL normalization (collapses "..", ".", relative
        // segments) — it does not follow reparse points. A junction or symlink planted inside a
        // configured folder can point anywhere: File.Exists/File.OpenRead/Directory.EnumerateFiles
        // all transparently follow it, so the file actually being served (or listed) can live
        // entirely outside every configured folder even though the lexical path looks contained.
        // We have to resolve the candidate to where it *really* is — walking every directory in
        // its ancestor chain, since the leaf or any directory above it can be the link — and
        // compare THAT against the configured roots (also resolved, so a legitimately-linked
        // root still works).
        var resolvedFull = ResolveReal(full);

        foreach (var folder in config.Folders)
        {
            // Trim trailing separators before combining with GetFullPath's own separator, or a
            // folder configured with one (e.g. "D:\Media\Movies\") doubles up and no genuinely
            // contained file's path ever matches — silently refusing everything inside it.
            var root = Path.GetFullPath(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var resolvedRoot = ResolveReal(root);

            if (resolvedFull.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, PathComparison) ||
                resolvedFull.Equals(resolvedRoot, PathComparison))
                return true;
        }
        return false;
    }

    /// <summary>Resolves a path to where it actually points on disk, the way the filesystem would
    /// when opening it: walks the path one segment at a time from the root, resolving every
    /// directory reparse point (junction/symlink) it passes through — not just the leaf — since
    /// the escape that matters here is a junction planted partway down the chain, not only at the
    /// end. Each resolved target is itself already fully resolved (ResolveLinkTarget follows a
    /// chain of links to its final destination), so subsequent segments build on the real
    /// location, not the linked one. A path with no links anywhere resolves to itself.</summary>
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

            // Intermediate segments are always directories. The leaf may be a file (the common
            // case, since ResolvePath already confirmed File.Exists) or itself a directory
            // reparse point, so try both there.
            var target = isLeaf
                ? File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName
                  ?? Directory.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName
                : Directory.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName;

            resolved = target ?? candidate;
        }

        return resolved;
    }

    private static string Describe(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB",
    };
}
