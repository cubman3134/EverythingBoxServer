using System.Security.Cryptography;
using System.Text;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Scraping;

/// <summary>
/// On-disk <see cref="IResolverCache"/> bounded by total size rather than age: each
/// entry is a file named by the hash of its key, under the configured directory.
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

        var path = PathFor(key);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Write to a temp file then move, so a reader never sees a half-written entry.
            // Written in chunks (rather than one File.WriteAllTextAsync call) so a
            // cancellation mid-write is actually observed: a single whole-value write
            // tends to land in the OS page cache and complete before the token has any
            // chance to be checked, regardless of when the caller cancelled it. Whatever
            // is left of the temp file when this throws — cancelled partway, or any other
            // failure — is cleaned up below so nothing is ever left behind.
            await WriteChunkedAsync(temp, value, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            EvictToFit();
        }
        catch
        {
            // best effort — and never leave an orphaned temp file behind, whatever the
            // cause (cancellation, disk error, etc).
            try { File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private const int WriteChunkChars = 64 * 1024;

    private static async Task WriteChunkedAsync(string path, string value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        for (var offset = 0; offset < value.Length; offset += WriteChunkChars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(WriteChunkChars, value.Length - offset);
            await writer.WriteAsync(value.AsMemory(offset, length), cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EvictToFit()
    {
        lock (_evictLock)
        {
            try
            {
                // Include orphaned "*.tmp" files (e.g. left behind by a process killed
                // mid-write, which no in-process cleanup can prevent) in both the size
                // accounting and eviction — otherwise they're invisible to the cap and
                // accumulate forever.
                var files = new DirectoryInfo(_directory).GetFiles("*.json")
                    .Concat(new DirectoryInfo(_directory).GetFiles("*.tmp"))
                    .ToArray();
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
