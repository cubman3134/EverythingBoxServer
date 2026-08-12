namespace EverythingBox.Server.RomLibrary;

/// <summary>One member file within a title: a concrete path plus the version that orders it
/// (higher = newer, null if unknown).</summary>
internal sealed record GroupMember(string Path, int? Version);

/// <summary>A base game and everything that hangs off it. A group forms ONLY around a present base —
/// <see cref="Updates"/> (newest first) and <see cref="Dlc"/> are the files that share the base's title id.
/// A file with no base to hang on becomes its own singleton (BasePath = itself, no members): the grouper
/// never invents a base.</summary>
internal sealed record TitleGroup(
    string BaseTitleId,
    string BasePath,
    IReadOnlyList<GroupMember> Updates,
    IReadOnlyList<GroupMember> Dlc);

internal static class TitleGrouper
{
    /// <summary>Group a folder's files into { base + update(s) + dlc } sets. Each file is identified and
    /// bucketed by title id; a bucket with a base yields one group (the largest base file heads it, its
    /// updates and DLC attach), a bucket with no base yields one singleton per orphan file, and a file with
    /// no identity yields a singleton. Result ordered by BasePath (ordinal). Pure apart from
    /// <see cref="TitleIdentifier.Identify"/>'s PS3 header read and a best-effort size check.</summary>
    public static IReadOnlyList<TitleGroup> Group(IEnumerable<string> files)
    {
        var groups = new List<TitleGroup>();
        var buckets = new Dictionary<string, List<(string Path, PackageIdentity Id)>>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            var id = TitleIdentifier.Identify(path);
            if (id is null)
            {
                // No identity at all → its own singleton. No title id exists; key it by its path.
                groups.Add(new TitleGroup(path, path, [], []));
                continue;
            }
            if (!buckets.TryGetValue(id.TitleId, out var list))
                buckets[id.TitleId] = list = new List<(string, PackageIdentity)>();
            list.Add((path, id));
        }

        foreach (var (titleId, members) in buckets)
        {
            var bases = members.Where(m => m.Id.Kind == TitleKind.Base).ToList();
            if (bases.Count == 0)
            {
                // Orphan updates / DLC with no base → never invent one; each is its own singleton.
                foreach (var m in members)
                    groups.Add(new TitleGroup(titleId, m.Path, [], []));
                continue;
            }

            // Several bases sharing an id are duplicates: keep the largest file, drop the rest.
            var basePath = bases
                .OrderByDescending(m => FileLength(m.Path))
                .ThenBy(m => m.Path, StringComparer.Ordinal)
                .First().Path;

            var updates = members
                .Where(m => m.Id.Kind == TitleKind.Update)
                .OrderByDescending(m => m.Id.Version ?? int.MinValue)   // newest first; null versions last
                .ThenBy(m => m.Path, StringComparer.Ordinal)
                .Select(m => new GroupMember(m.Path, m.Id.Version))
                .ToList();

            var dlc = members
                .Where(m => m.Id.Kind == TitleKind.Dlc)
                .OrderBy(m => m.Path, StringComparer.Ordinal)
                .Select(m => new GroupMember(m.Path, m.Id.Version))
                .ToList();

            groups.Add(new TitleGroup(titleId, basePath, updates, dlc));
        }

        groups.Sort((a, b) => string.CompareOrdinal(a.BasePath, b.BasePath));
        return groups;
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0L; } // best-effort: a name-only or unreadable path just sorts as size 0
    }
}
