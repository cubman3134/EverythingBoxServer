using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EverythingBox.Server.Sync;

/// <summary>
/// A dumb, versioned, per-namespace object store: plain blob files + one index.json per namespace.
/// Keys are hashed to blob filenames (a client key is never a path segment → no traversal). A
/// per-namespace lock serialises mutations so check-version→write→bump is atomic. The store never
/// reads a payload; it stores bytes + an opaque per-object version and an opaque client meta string.
/// </summary>
public sealed partial class SyncStore
{
    private readonly string _root;
    private readonly long _perNamespaceQuotaBytes;
    private readonly long _maxObjectBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public SyncStore(string rootDir, long perNamespaceQuotaBytes, long maxObjectBytes)
    {
        _root = rootDir;
        _perNamespaceQuotaBytes = perNamespaceQuotaBytes;
        _maxObjectBytes = maxObjectBytes;
        try { Directory.CreateDirectory(_root); } catch { /* created lazily on first write too */ }
    }

    /// <summary>The configured per-object byte cap. Exposed so the PUT route can raise Kestrel's
    /// per-request body limit to it — the store's own <see cref="CopyCappedAsync"/> still enforces
    /// the cap, but without this the small default limit would 413 large bodies before we count them.</summary>
    public long MaxObjectBytes => _maxObjectBytes;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamespacePattern();

    public static bool IsValidNamespace(string ns)
        // A trailing '.' is stripped by Windows path resolution ("a." and "a" name the same directory),
        // so it would get a different per-namespace lock yet share the index — reject it outright.
        => !string.IsNullOrEmpty(ns) && ns is not ("." or "..") && NamespacePattern().IsMatch(ns) && !ns.EndsWith('.');

    // ---- internal index model (persisted as index.json) ----
    private sealed class IndexEntry
    {
        public string Version { get; set; } = "";
        public string? Meta { get; set; }
        public long Size { get; set; }
        public bool Deleted { get; set; }
        public DateTime ModifiedUtc { get; set; }
    }
    private sealed class Index { public Dictionary<string, IndexEntry> Objects { get; set; } = new(StringComparer.Ordinal); }

    private SemaphoreSlim LockFor(string ns) => _locks.GetOrAdd(ns, _ => new SemaphoreSlim(1, 1));

    // The namespace directory, containment-checked under _root. Null if the namespace is invalid or
    // (defensively) resolves outside the root.
    private string? NamespaceDir(string ns)
    {
        if (!IsValidNamespace(ns)) return null;
        string full, rootFull;
        try { full = Path.GetFullPath(Path.Combine(_root, ns)); rootFull = Path.GetFullPath(_root); }
        catch { return null; }
        var boundary = rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(boundary, cmp) ? full : null;
    }

    private static string BlobName(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    private static Index LoadIndex(string nsDir)
    {
        var path = Path.Combine(nsDir, "index.json");
        try
        {
            if (!File.Exists(path)) return new Index();
            return JsonSerializer.Deserialize<Index>(File.ReadAllText(path), Json) ?? new Index();
        }
        catch { return new Index(); } // a corrupt index degrades to empty rather than throwing out of a request
    }

    private static void SaveIndex(string nsDir, Index index)
    {
        Directory.CreateDirectory(nsDir);
        var path = Path.Combine(nsDir, "index.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(index, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch { TryDelete(temp); throw; } // a failed write/move (e.g. disk full) must not orphan the .tmp
    }

    public async Task<IReadOnlyList<SyncObjectInfo>> ListAsync(string ns, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return [];
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            return index.Objects
                .Select(kv => new SyncObjectInfo(kv.Key, kv.Value.Version, kv.Value.Meta, kv.Value.Size, kv.Value.Deleted, kv.Value.ModifiedUtc))
                .ToList();
        }
        finally { gate.Release(); }
    }

    public async Task<SyncObjectContent?> GetAsync(string ns, string key, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return null;
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            if (!index.Objects.TryGetValue(key, out var e) || e.Deleted) return null;
            var blob = Path.Combine(nsDir, BlobName(key));
            if (!File.Exists(blob)) return null;
            return new SyncObjectContent(blob, e.Version, e.Meta, e.Size);
        }
        finally { gate.Release(); }
    }

    public async Task<SyncWriteOutcome> PutAsync(string ns, string key, Stream body, SyncCondition condition, string? meta, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed); // invalid ns → caller 400s first; defensive
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            index.Objects.TryGetValue(key, out var existing);

            if (!ConditionSatisfied(condition, existing))
                return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed);

            // Stream to a temp file, counting bytes and enforcing the per-object cap without buffering.
            Directory.CreateDirectory(nsDir);
            var temp = Path.Combine(nsDir, BlobName(key) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            long size;
            try { size = await CopyCappedAsync(body, temp, _maxObjectBytes, ct).ConfigureAwait(false); }
            catch (TooLargeException) { TryDelete(temp); return new SyncWriteOutcome(SyncWriteStatus.TooLarge); }
            catch { TryDelete(temp); throw; }

            // Quota: sum of LIVE object sizes, replacing this key's old contribution.
            var oldContribution = existing is { Deleted: false } ? existing.Size : 0;
            var liveTotal = index.Objects.Values.Where(v => !v.Deleted).Sum(v => v.Size);
            if (liveTotal - oldContribution + size > _perNamespaceQuotaBytes)
            {
                TryDelete(temp);
                return new SyncWriteOutcome(SyncWriteStatus.QuotaExceeded);
            }

            var blob = Path.Combine(nsDir, BlobName(key));
            try { File.Move(temp, blob, overwrite: true); }
            catch { TryDelete(temp); throw; } // e.g. a Windows sharing violation while another request streams the blob — never orphan the temp
            var version = NewVersion();
            index.Objects[key] = new IndexEntry { Version = version, Meta = meta, Size = size, Deleted = false, ModifiedUtc = DateTime.UtcNow };
            SaveIndex(nsDir, index);
            return new SyncWriteOutcome(SyncWriteStatus.Ok, version);
        }
        finally { gate.Release(); }
    }

    public async Task<SyncWriteOutcome> DeleteAsync(string ns, string key, SyncCondition condition, CancellationToken ct)
    {
        var nsDir = NamespaceDir(ns);
        if (nsDir is null) return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed);
        var gate = LockFor(ns);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = LoadIndex(nsDir);
            index.Objects.TryGetValue(key, out var existing);
            if (!ConditionSatisfied(condition, existing))
                return new SyncWriteOutcome(SyncWriteStatus.PreconditionFailed);

            TryDelete(Path.Combine(nsDir, BlobName(key))); // free the bytes; keep a tombstone in the index
            var version = NewVersion();
            index.Objects[key] = new IndexEntry { Version = version, Meta = existing?.Meta, Size = 0, Deleted = true, ModifiedUtc = DateTime.UtcNow };
            SaveIndex(nsDir, index);
            return new SyncWriteOutcome(SyncWriteStatus.Ok, version);
        }
        finally { gate.Release(); }
    }

    // A live object counts as "present" for If-None-Match:*; a tombstone counts as absent (re-creatable).
    private static bool ConditionSatisfied(SyncCondition c, IndexEntry? existing) => c.Kind switch
    {
        SyncConditionKind.Unconditional => true,
        SyncConditionKind.IfNoneMatchStar => existing is null || existing.Deleted,
        SyncConditionKind.IfMatch => existing is not null && existing.Version == c.Version,
        _ => false,
    };

    private sealed class TooLargeException : Exception { }

    private static async Task<long> CopyCappedAsync(Stream src, string destPath, long cap, CancellationToken ct)
    {
        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap) throw new TooLargeException();
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        await dest.FlushAsync(ct).ConfigureAwait(false);
        return total;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { /* best effort */ } }
}
