using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;

namespace EverythingBox.Server.Tests;

/// <summary>
/// C1: DeferredTorrentGrabber replaces a Lazy&lt;T&gt;-over-a-mutable-list approach that
/// silently poisoned the grabber forever if any plugin called Server.Grabber from its own
/// Configure — it built and permanently cached a grabber with zero indexers, so every later
/// request-time call from every plugin quietly returned nothing, with no exception and no
/// log. This must fail loudly at the point of the mistake instead.
/// </summary>
public class DeferredTorrentGrabberTests
{
    private static MovieRequest Req => new() { Title = "The Matrix", Year = 1999 };

    [Fact]
    public async Task Calling_before_SetGrabber_throws_and_mentions_registration()
    {
        var deferred = new DeferredTorrentGrabber();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => deferred.SearchAsync(Req));
        Assert.Contains("registration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GrabAsync_before_SetGrabber_also_throws()
    {
        var deferred = new DeferredTorrentGrabber();
        await Assert.ThrowsAsync<InvalidOperationException>(() => deferred.GrabAsync(Req));
    }

    /// <summary>
    /// The exact bad case from C1: a plugin whose Configure reaches through
    /// IPluginContext.Server.Grabber instead of stashing the reference for later. Wires
    /// DeferredTorrentGrabber + ServerServices + PluginContext exactly as Program.cs does,
    /// then runs a plugin's Configure that touches Server.Grabber immediately — proving the
    /// mistake now surfaces right there as a loud, named exception instead of silently
    /// building and caching an empty grabber for the rest of the process's life.
    /// </summary>
    /// <summary>A plugin, exactly as PluginHost would instantiate one, whose Configure
    /// reaches for the grabber immediately instead of stashing the reference for later —
    /// the precise mistake C1 is about.</summary>
    private sealed class EagerGrabberTouchingPlugin : IPlugin
    {
        public string Key => "eager";
        public string DisplayName => "Eager Grabber-Touching Plugin";
        public Version ApiVersion => new(ServerApi.VersionString);

        public void Configure(IPluginRegistry registry, IPluginContext context)
            => context.Server.Grabber.SearchAsync(Req).GetAwaiter().GetResult();
    }

    [Fact]
    public void A_plugin_touching_Server_Grabber_during_Configure_fails_loudly_at_that_call()
    {
        var deferred = new DeferredTorrentGrabber();
        var services = new ServerServices(deferred, debrid: null, files: new StubFileCache());
        using var http = new HttpClient();
        var context = new PluginContext(
            "eager", new ServerConfig(), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            http, Path.Combine(Path.GetTempPath(), "ebs-eager-" + Guid.NewGuid().ToString("N")), services);

        IPlugin plugin = new EagerGrabberTouchingPlugin();
        var ex = Record.Exception(() => plugin.Configure(new PluginRegistry(), context));

        var ioe = Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("registration", ioe.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Configure", ioe.Message);
    }

    [Fact]
    public async Task After_SetGrabber_calls_delegate_to_the_real_grabber()
    {
        var deferred = new DeferredTorrentGrabber();
        var real = new FakeGrabber();
        deferred.SetGrabber(real);

        var result = await deferred.SearchAsync(Req);

        Assert.Same(real.SearchResult, result);
        Assert.Equal(1, real.SearchCalls);
    }

    [Fact]
    public void Setting_the_grabber_twice_throws()
    {
        var deferred = new DeferredTorrentGrabber();
        deferred.SetGrabber(new FakeGrabber());

        Assert.Throws<InvalidOperationException>(() => deferred.SetGrabber(new FakeGrabber()));
    }

    // Dispatches every racer on its own thread-pool thread, held at a barrier so they all
    // call SearchAsync at (as close as possible to) the same instant. Racers built with plain
    // Task.Run-less dispatch (or a LINQ .Select with no Task.Run) would all run their
    // synchronous prefix on the single calling thread, proving nothing about real concurrent
    // access — see FileCacheTests.Genuinely_concurrent_callers_on_separate_threads_build_once
    // for the same lesson learned once already in this repo.
    [Fact]
    public async Task Concurrent_callers_after_SetGrabber_all_reach_the_same_instance()
    {
        var deferred = new DeferredTorrentGrabber();
        var real = new FakeGrabber();
        deferred.SetGrabber(real);

        const int callerCount = 32;
        using var barrier = new Barrier(callerCount);

        var racers = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await deferred.SearchAsync(Req);
            }))
            .ToArray();

        var results = await Task.WhenAll(racers);

        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Same(real.SearchResult, r));
        Assert.Equal(callerCount, real.SearchCalls);
    }

    private sealed class FakeGrabber : ITorrentGrabber
    {
        public int SearchCalls;
        public readonly IReadOnlyList<TorrentResult> SearchResult = [];

        public Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new GrabResult());

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref SearchCalls);
            return Task.FromResult(SearchResult);
        }
    }

    private sealed class StubFileCache : IFileCache
    {
        public string Root => Path.GetTempPath();

        public Task<BuiltFile?> GetOrBuildAsync(string servedName, Func<string, CancellationToken, Task<BuiltFile?>> build, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not used by this test.");
    }
}
