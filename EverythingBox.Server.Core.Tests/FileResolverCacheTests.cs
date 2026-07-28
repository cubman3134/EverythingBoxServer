using EverythingBox.Server.Core.Scraping;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class FileResolverCacheTests
{
    [Fact]
    public async Task RoundTripsAndMissesUnknownKeys()
    {
        var dir = NewDir();
        try
        {
            var cache = new FileResolverCache(dir, 10 * 1024 * 1024);
            await cache.SetAsync("k", "value-1");

            Assert.Equal("value-1", await cache.GetAsync("k"));
            Assert.Null(await cache.GetAsync("does-not-exist"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task EvictsToStayUnderSizeCap()
    {
        var dir = NewDir();
        try
        {
            // Cap fits ~2 of these 1 KB entries; writing 6 must evict down under the cap.
            var cache = new FileResolverCache(dir, maxBytes: 2500);
            for (var i = 0; i < 6; i++)
                await cache.SetAsync($"key-{i}", new string((char)('a' + i), 1000));

            var total = Directory.GetFiles(dir, "*.json").Sum(f => new FileInfo(f).Length);
            Assert.True(total <= 2500, $"cache size {total} exceeded the cap");

            // The most recently written entry survives.
            Assert.NotNull(await cache.GetAsync("key-5"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedFirst()
    {
        var dir = NewDir();
        try
        {
            var cache = new FileResolverCache(dir, maxBytes: 2500); // fits 2 of 1 KB, not 3
            await cache.SetAsync("a", new string('A', 1000));
            await cache.SetAsync("b", new string('B', 1000));

            // Make "a" the oldest so it's evicted first when the next write overflows.
            Backdate(dir, "AAAA", DateTime.UtcNow.AddHours(-2));
            await cache.SetAsync("c", new string('C', 1000));

            Assert.Null(await cache.GetAsync("a"));      // evicted (least recently used)
            Assert.NotNull(await cache.GetAsync("b"));
            Assert.NotNull(await cache.GetAsync("c"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task SizeZeroDisablesWrites()
    {
        var dir = NewDir();
        try
        {
            var cache = new FileResolverCache(dir, maxBytes: 0);
            await cache.SetAsync("k", "v");
            Assert.Null(await cache.GetAsync("k"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task CancelledWriteLeavesNoTempFileBehind()
    {
        var dir = NewDir();
        try
        {
            // Cap is well above the value size so eviction never enters into it —
            // this test is only about what a cancellation leaves behind.
            var cache = new FileResolverCache(dir, maxBytes: 100 * 1024 * 1024);
            using var cts = new CancellationTokenSource();

            // Cancel as soon as the temp file is created on disk (well before a
            // multi-megabyte value finishes writing), so the write is guaranteed to be
            // interrupted rather than racing it to completion.
            using var watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
            watcher.Created += (_, e) =>
            {
                if (e.Name is not null && e.Name.EndsWith(".tmp", StringComparison.Ordinal))
                    cts.Cancel();
            };

            var bigValue = new string('x', 20 * 1024 * 1024); // 20 MB: ample margin for the watcher event to land first
            await cache.SetAsync("k", bigValue, cts.Token);

            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Null(await cache.GetAsync("k")); // the cancelled write never published an entry either
        }
        finally { Cleanup(dir); }
    }

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "ebs-cache-test-" + Guid.NewGuid().ToString("N"));
    private static void Cleanup(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    private static void Backdate(string dir, string contentMarker, DateTime timeUtc)
    {
        foreach (var f in Directory.GetFiles(dir, "*.json"))
            if (File.ReadAllText(f).Contains(contentMarker))
            {
                File.SetLastWriteTimeUtc(f, timeUtc);
                return;
            }
    }
}
