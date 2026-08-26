using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

/// <summary>Where a fetched romhack file waits until the client collects it, and how long it waits.
///
/// <para>A staged file has to outlive the response that names it, which the previous design never had
/// to do: it read the bytes into the reply and deleted the directory in the same call, so the file's
/// whole life was one function call. That is the real cost of serving by URL, and this type is where
/// it is paid.</para>
///
/// <para>The root is fixed, not a preference. <see cref="SafeLocalFileServer"/>'s roots are fixed at
/// construction, so a fresh GUID directory per fetch under the OS temp path can never be served — it
/// could not be in a fixed root list. One staging root with a per-fetch subdirectory inside it is what
/// makes serving possible at all.</para>
///
/// <para><b>Retention is age-based, and that is a limitation, not a design preference.</b> The moment
/// to delete would be when the client confirms the install — but nothing in the protocol reports one,
/// and inventing a confirmation channel is a larger change than this. So a file is kept for a fixed
/// window and swept afterwards whether it was collected or not, and a client that waits longer than
/// the window gets a 404. That is by design, not a bug to be reported.</para>
///
/// <para>Nothing here throws for an ordinary filesystem failure: a sweep over a root that is not there
/// is zero, and a directory that is locked or vanished mid-sweep is left for next time. Sweeping is
/// housekeeping, and housekeeping that can fail a request is worse than housekeeping that skips a
/// directory.</para></summary>
public sealed class RomhackStaging
{
    private readonly TimeSpan _retention;

    // Containment has exactly one implementation in this codebase, and it is the one four plugins
    // already serve files through. Scoping an instance to the staging root reuses that check — the
    // decode, the real-resolve through junctions and symlinks, the comparison — rather than adding a
    // second path check that could drift away from it.
    private readonly SafeLocalFileServer _containment;

    public RomhackStaging(string root, TimeSpan retention)
    {
        // Trim before GetFullPath re-adds its own separator: a root configured with a trailing one
        // ("D:\staging\") would otherwise double up and nothing inside it would ever match.
        Root = Path.GetFullPath(Path.TrimEndingDirectorySeparator(root));
        _retention = retention;
        _containment = new SafeLocalFileServer([Root], _ => "application/octet-stream");
    }

    public string Root { get; }

    /// <summary>A fresh directory for one fetch. Inside the root, because only the root is served.</summary>
    public string NewFetchDirectory()
    {
        var dir = Path.Combine(Root, "fetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Real-resolved containment, so neither a ".." nor a junction can answer yes, and
    /// neither can a SIBLING whose name merely starts with the root's — "&lt;root&gt;-evil" is not
    /// under "&lt;root&gt;", which a prefix comparison without the separator would get wrong.
    ///
    /// <para>Resolution follows reparse points, which needs a path that exists; a staged file that has
    /// not been written yet is judged by the deepest ancestor that does exist, since only an existing
    /// ancestor can redirect the path anywhere.</para></summary>
    public bool IsInsideRoot(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(absolutePath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return false;
        }

        try
        {
            return _containment.IsContained(DeepestExisting(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The root itself is missing, or a segment became unreadable mid-check. Refusing is the
            // safe answer: nothing is served out of a root that cannot be resolved.
            return false;
        }
    }

    /// <summary>Drops fetch directories older than the retention. Returns how many went.
    ///
    /// <para>Never removes the root — only its children are candidates — and a missing root is simply
    /// nothing to do rather than an error.</para></summary>
    public int Sweep(DateTimeOffset now)
    {
        string[] candidates;
        try
        {
            if (!Directory.Exists(Root)) return 0;
            // Snapshot rather than deleting mid-enumeration, so a delete cannot disturb the walk.
            candidates = Directory.GetDirectories(Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var dir in candidates)
        {
            try
            {
                var age = now - new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
                if (age <= _retention) continue;

                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, or vanished under us. Leaving a file behind costs disk until the next
                // sweep; racing a reader costs the download. Leave it.
            }
        }
        return removed;
    }

    /// <summary>The path itself if it exists, otherwise its nearest existing ancestor. Bottoms out at
    /// the path root, which always terminates the walk even when nothing on it exists.</summary>
    private static string DeepestExisting(string full)
    {
        var probe = full;
        while (!Directory.Exists(probe) && !File.Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || parent.Equals(probe, StringComparison.Ordinal)) return probe;
            probe = parent;
        }
        return probe;
    }
}
