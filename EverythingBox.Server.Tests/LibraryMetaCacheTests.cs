using System.Collections.Concurrent;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

public class LibraryMetaCacheTests : IDisposable
{
    // A small public shape to exercise the now-generic cache independently of any plugin type.
    private sealed record TestMeta(string? NfoTitle, int? Year, string? Plot, string? PosterPath);

    private sealed class MemoryCache : IResolverCache
    {
        public ConcurrentDictionary<string, string> Store { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { Store[key] = value; return Task.CompletedTask; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-mc-" + Guid.NewGuid().ToString("N"));
    public LibraryMetaCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private string WriteFile(string name, byte[] bytes) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, bytes); return p; }

    private static (Func<TestMeta> compute, Func<int> count) Counting(TestMeta value)
    {
        var n = 0;
        return (() => { n++; return value; }, () => n);
    }

    [Fact]
    public async Task Null_cache_always_computes()
    {
        var cache = new LibraryMetaCache(null);
        var (compute, count) = Counting(new TestMeta("T", 2020, "P", null));
        var media = WriteFile("a.mkv", [1]);
        await cache.GetOrComputeAsync(media, null, compute, default);
        await cache.GetOrComputeAsync(media, null, compute, default);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task A_hit_does_not_recompute_for_an_unchanged_file()
    {
        var store = new MemoryCache();
        var cache = new LibraryMetaCache(store);
        var media = WriteFile("b.mkv", [1]);
        var (compute, count) = Counting(new TestMeta("Title", 1999, "Plot", "/p.jpg"));

        var first = await cache.GetOrComputeAsync(media, null, compute, default);
        var second = await cache.GetOrComputeAsync(media, null, compute, default);

        Assert.Equal(1, count());               // computed once, hit second time
        Assert.Equal(first, second);
        Assert.Equal("Title", second.NfoTitle);
        Assert.Equal(1999, second.Year);
        Assert.Equal("/p.jpg", second.PosterPath);
    }

    [Fact]
    public async Task Changing_the_media_mtime_recomputes()
    {
        var cache = new LibraryMetaCache(new MemoryCache());
        var media = WriteFile("c.mkv", [1]);
        var (compute, count) = Counting(new TestMeta("X", null, null, null));
        await cache.GetOrComputeAsync(media, null, compute, default);
        File.SetLastWriteTimeUtc(media, File.GetLastWriteTimeUtc(media).AddSeconds(5));
        await cache.GetOrComputeAsync(media, null, compute, default);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task Changing_the_nfo_mtime_recomputes_even_if_media_is_unchanged()
    {
        var cache = new LibraryMetaCache(new MemoryCache());
        var media = WriteFile("d.mkv", [1]);
        var nfo = WriteFile("d.nfo", [2]);
        var (compute, count) = Counting(new TestMeta("X", null, null, null));
        await cache.GetOrComputeAsync(media, nfo, compute, default);
        File.SetLastWriteTimeUtc(nfo, File.GetLastWriteTimeUtc(nfo).AddSeconds(5));
        await cache.GetOrComputeAsync(media, nfo, compute, default);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task A_corrupt_cached_value_is_recomputed()
    {
        var store = new MemoryCache();
        var cache = new LibraryMetaCache(store);
        var media = WriteFile("e.mkv", [1]);
        var key = $"{media}|{File.GetLastWriteTimeUtc(media).Ticks}|0";
        store.Store[key] = "not json";
        var (compute, count) = Counting(new TestMeta("Recovered", null, null, null));
        var result = await cache.GetOrComputeAsync(media, null, compute, default);
        Assert.Equal(1, count());
        Assert.Equal("Recovered", result.NfoTitle);
    }
}
