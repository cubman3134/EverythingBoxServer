using System.Text.Json;

namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Memoizes a computed value by (media path, media mtime, nfo mtime) so an unchanged file is not
/// re-parsed on every browse. Backed by an optional <see cref="IResolverCache"/> — null (unit tests)
/// means always compute. Best-effort: any cache error recomputes; nothing throws out.
/// </summary>
public sealed class LibraryMetaCache(IResolverCache? cache)
{
    private static readonly JsonSerializerOptions Json = new();

    public async Task<T> GetOrComputeAsync<T>(
        string mediaPath, string? nfoPath, Func<T> compute, CancellationToken ct)
    {
        if (cache is null) return compute();

        var key = $"{mediaPath}|{File.GetLastWriteTimeUtc(mediaPath).Ticks}|" +
                  (nfoPath is null ? "0" : File.GetLastWriteTimeUtc(nfoPath).Ticks.ToString());

        // Reading the cache is best-effort too: a non-conforming IResolverCache that throws on read
        // (or a corrupt value) must fall through to recompute, not escape. Cancellation is the one
        // exception that MUST propagate — it is not a cache failure.
        try
        {
            var hit = await cache.GetAsync(key, ct).ConfigureAwait(false);
            if (hit is not null && JsonSerializer.Deserialize<T>(hit, Json) is { } cached)
                return cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* miss → recompute below */ }

        var computed = compute();
        try { await cache.SetAsync(key, JsonSerializer.Serialize(computed, Json), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort: a cache write must never fail a browse */ }
        return computed;
    }
}
