using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

public class FileCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-filecache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private FileCache NewCache() => new(_root);

    private async Task<BuiltFile?> WriteAsync(string name, string contents, CancellationToken ct)
    {
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, contents, ct);
        return new BuiltFile(name, path, "text/plain");
    }

    [Fact]
    public async Task Builds_a_file_then_serves_it_from_disk()
    {
        var cache = NewCache();
        var builds = 0;

        Task<BuiltFile?> Build(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref builds);
            return WriteAsync(name, "hello", ct);
        }

        var first = await cache.GetOrBuildAsync("a.txt", Build, CancellationToken.None);
        var second = await cache.GetOrBuildAsync("a.txt", Build, CancellationToken.None);

        Assert.Equal("hello", await File.ReadAllTextAsync(first!.Path));
        Assert.Equal(first.Path, second!.Path);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_file_build_once()
    {
        var cache = NewCache();
        var builds = 0;
        var gate = new TaskCompletionSource();

        async Task<BuiltFile?> SlowBuild(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref builds);
            await gate.Task;
            return await WriteAsync(name, "slow", ct);
        }

        var racers = Enumerable.Range(0, 10)
            .Select(_ => cache.GetOrBuildAsync("b.txt", SlowBuild, CancellationToken.None))
            .ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(racers);

        Assert.Equal(1, builds);
        Assert.All(results, r => Assert.Equal(results[0]!.Path, r!.Path));
    }

    // Concurrent_requests_for_the_same_file_build_once dispatches its racers with a plain
    // LINQ .Select — with no Task.Run, each GetOrBuildAsync call's synchronous prefix (which
    // includes ConcurrentDictionary.GetOrAdd invoking its value factory) runs to completion
    // on the single calling thread before the next racer's call even begins. Only one thread
    // ever touches the builder, so that test cannot detect GetOrAdd's factory running
    // concurrently on real threads — which is exactly what happens under real request
    // contention. This test dispatches each racer on its own thread-pool thread and holds
    // them at a barrier so they all call GetOrBuildAsync at (as close as possible to) the
    // same instant, to actually exercise that race.
    [Fact]
    public async Task Genuinely_concurrent_callers_on_separate_threads_build_once()
    {
        var cache = NewCache();
        var builds = 0;
        const int callerCount = 16;
        using var barrier = new Barrier(callerCount);

        Task<BuiltFile?> Build(string name, CancellationToken ct)
        {
            Interlocked.Increment(ref builds);
            return WriteAsync(name, "racy", ct);
        }

        var racers = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(() =>
            {
                // Every caller waits here so they enter GetOrBuildAsync — and therefore
                // GetOrAdd's factory — as close to simultaneously as the OS scheduler allows.
                barrier.SignalAndWait();
                return cache.GetOrBuildAsync("race.txt", Build, CancellationToken.None);
            }))
            .ToArray();

        var results = await Task.WhenAll(racers);

        Assert.Equal(1, builds);
        Assert.All(results, r => Assert.Equal(results[0]!.Path, r!.Path));
    }

    [Fact]
    public async Task A_failed_build_is_not_cached()
    {
        var cache = NewCache();
        var attempts = 0;

        Task<BuiltFile?> Flaky(string name, CancellationToken ct)
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("first attempt fails")
                : WriteAsync(name, "second", ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrBuildAsync("c.txt", Flaky, CancellationToken.None));

        // The failure must not poison the entry — a retry has to be able to succeed.
        var recovered = await cache.GetOrBuildAsync("c.txt", Flaky, CancellationToken.None);
        Assert.Equal("second", await File.ReadAllTextAsync(recovered!.Path));
    }

    // F7: a null result is a transient miss, not a permanent one. Before the fix, eviction
    // happened only in the catch block, so a build that legitimately returns null got cached
    // forever and every later call saw null without the builder running again.
    [Fact]
    public async Task A_null_result_is_not_cached_and_a_later_call_can_still_succeed()
    {
        var cache = NewCache();
        var attempts = 0;

        Task<BuiltFile?> MissThenHit(string name, CancellationToken ct)
        {
            attempts++;
            return attempts == 1
                ? Task.FromResult<BuiltFile?>(null)
                : WriteAsync(name, "second", ct);
        }

        var first = await cache.GetOrBuildAsync("d.txt", MissThenHit, CancellationToken.None);
        Assert.Null(first);

        var second = await cache.GetOrBuildAsync("d.txt", MissThenHit, CancellationToken.None);
        Assert.Equal("second", await File.ReadAllTextAsync(second!.Path));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/dir.txt")]
    [InlineData("")]
    public async Task Refuses_a_name_that_is_not_a_plain_file_name(string name)
    {
        var cache = NewCache();
        await Assert.ThrowsAsync<ArgumentException>(
            () => cache.GetOrBuildAsync(name, (n, c) => WriteAsync(n, "x", c), CancellationToken.None));
    }
}
