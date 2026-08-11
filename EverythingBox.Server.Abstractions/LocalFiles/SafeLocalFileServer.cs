using System.Text;

namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Serves bytes from a fixed set of configured roots with HTTP Range support, and is the single
/// place the containment discipline lives: every id that arrives from a client is decoded,
/// real-resolved (following junctions/symlinks) and confirmed to live inside a configured root
/// before anything is served — an id is never trusted on its own. The set of roots is fixed at
/// construction; a caller scopes which roots apply (e.g. serving over all roots vs. directory
/// resolution over series roots only) by building a separate instance per scope.
/// </summary>
public sealed class SafeLocalFileServer
{
    private readonly IReadOnlyList<string> _roots;
    private readonly Func<string, string> _mimeFor;

    public SafeLocalFileServer(IReadOnlyList<string> roots, Func<string, string> mimeFor)
    {
        _roots = [.. roots];
        _mimeFor = mimeFor;
    }

    public static string EncodeId(string absolutePath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(absolutePath)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? TryDecodeId(string id)
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
    public string? ResolveSafeFile(string itemId)
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

    /// <summary>Decodes an id and confirms it is a real directory strictly UNDER a configured
    /// root — gating directory expansion so an arbitrary or foreign folder id can never be
    /// enumerated. A show is always a strict subfolder of a root, so the root itself is rejected.
    /// Null for any bad id, a file, a root, or a directory outside the roots.</summary>
    public string? ResolveSafeDir(string itemId)
    {
        if (TryDecodeId(itemId) is not { } decoded) return null;

        string full;
        try { full = Path.GetFullPath(decoded); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }

        if (!Directory.Exists(full)) return null;

        var resolved = ResolveReal(full);
        foreach (var root in _roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string r;
            try { r = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { continue; }
            var resolvedRoot = ResolveReal(r);
            // A show is always a strict SUBFOLDER of a series root; the root itself is never a show.
            // Accept only a directory strictly UNDER a root — an id forged for the root must not
            // flatten the whole root into episodes. (FILE serving via ResolveSafeFile/IsContained is
            // unchanged: a file directly in a root must still serve.)
            if (resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, PathComparison))
                return resolved;
        }
        return null;
    }

    /// <summary>True if <paramref name="full"/> — a path already confirmed to exist — actually
    /// resolves, after following every reparse point in its ancestor chain, to somewhere inside a
    /// configured root (also resolved the same way). Shared by ResolveSafeFile (a decoded client id)
    /// and the caller's enumeration backstop (every enumerated path) so opening and listing can
    /// never diverge.</summary>
    public bool IsContained(string full)
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

        foreach (var folder in _roots)
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

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct = default)
    {
        var path = ResolveSafeFile(itemId);
        if (path is null) return Task.FromResult<ProxyResponse?>(null);

        var info = new FileInfo(path);
        if (!info.Exists) return Task.FromResult<ProxyResponse?>(null);

        var total = info.Length;
        var mime = _mimeFor(path);
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
