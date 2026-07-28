using System.Security.Cryptography;
using System.Text;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Scraping;

/// <summary>
/// On-disk <see cref="IResolverCache"/> bounded by total size rather than age: each
/// entry is a file named by the hash of its key, under <paramref name="directory"/>.
/// When a write pushes the directory past <c>maxBytes</c>, the least-recently-used
/// entries are evicted until it fits again (a read refreshes an entry's recency).
/// Used to cache search results, item metadata, and ZIP directory listings so
/// repeated/similar searches skip slow upstream round-trips. All operations are
/// best-effort and never throw.
/// </summary>
public sealed class FileResolverCache : IResolverCache
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly Lock _evictLock = new();

    public FileResolverCache(string directory, long maxBytes)
    {
        _directory = directory;
        _maxBytes = Math.Max(0, maxBytes);
        try { Directory.CreateDirectory(_directory); } catch { /* best effort */ }
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path))
                return null;
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            // Refresh recency so frequently-used entries survive eviction (LRU).
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* ignore */ }
            return text;
        }
        catch
        {
            return null;
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        if (_maxBytes == 0)
            return;
        try
        {
            // Write to a temp file then move, so a reader never sees a half-written entry.
            var path = PathFor(key);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, value, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            EvictToFit();
        }
        catch
        {
            // best effort
        }
    }

    private void EvictToFit()
    {
        lock (_evictLock)
        {
            try
            {
                var files = new DirectoryInfo(_directory).GetFiles("*.json");
                var total = files.Sum(f => f.Length);
                if (total <= _maxBytes)
                    return;

                // Drop the oldest (least-recently-used) entries until we're under the cap.
                foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= _maxBytes)
                        break;
                    try { total -= file.Length; file.Delete(); } catch { /* ignore */ }
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, hash + ".json");
    }
}
