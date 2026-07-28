using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// F6: IMediaSource.WarmUpAsync was in the public plugin contract but nothing ever called
/// it — a plugin author implementing it got silent dead code. Program.cs now calls it once
/// per registered source at startup. TestPlugin.Good's GoodSource writes a marker file from
/// inside WarmUpAsync (into its plugin cache directory, which the test can see from outside
/// the plugin's own AssemblyLoadContext) so this can be verified without reaching into
/// plugin-internal state.
/// </summary>
[Collection(AddonServerCollection.Name)]
public class WarmUpTests
{
    private readonly PluginServerFactory _factory;
    public WarmUpTests(PluginServerFactory factory) => _factory = factory;

    [Fact]
    public async Task A_registered_sources_WarmUpAsync_runs_at_startup()
    {
        // Any request forces the host to actually start — that is where Program.cs's
        // top-level statements (plugin loading, then the warm-up loop) run, the same
        // mechanism the eager SourceRouter resolution already relies on.
        var health = await _factory.CreateClient().GetAsync("/health");
        Assert.True(health.IsSuccessStatusCode);

        var marker = Path.Combine(_factory.FilesDirectory, "plugins", "good", "warmup.marker");
        Assert.True(File.Exists(marker), $"Expected GoodSource.WarmUpAsync to have run and written {marker}");
    }
}

/// <summary>I2: a source whose WarmUpAsync never returns used to hang Program.cs's startup
/// loop forever — no "Listening on" line, /health unreachable indefinitely, because the
/// call was awaited with no bound. This is a deliberately NOT-over-HTTP test: driving it
/// through PluginServerFactory would mean every test in the shared AddonServerCollection
/// pays SourceWarmUp.DefaultTimeout (15s) once at host start-up, and the real,
/// end-to-end "does the actual server listen anyway" claim was verified separately against
/// the built server itself. This unit test proves the underlying mechanism Program.cs
/// relies on: SourceWarmUp.RunAsync must return once ITS OWN timeout elapses, not once the
/// hung task happens to finish (it never does).</summary>
file sealed class HangingWarmUpSource : IMediaSource
{
    public string Key => "hangingwarmup";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);

    // Never completes — CancellationToken.None means nothing this test does can complete it
    // either, matching how Program.cs actually calls WarmUpAsync (see SourceWarmUp.RunAsync).
    public Task<WarmUpResult> WarmUpAsync(CancellationToken ct) => new TaskCompletionSource<WarmUpResult>().Task;
}

/// <summary>Captures everything logged, so a test can assert a specific warning fired
/// without depending on any particular logging provider. Duplicated from SourceRouterTests'
/// file-scoped copy — "file" visibility does not cross files.</summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}

public class SourceWarmUpTests
{
    [Fact]
    public async Task A_hanging_WarmUpAsync_does_not_block_forever()
    {
        var bound = SourceWarmUp.RunAsync(
            new HangingWarmUpSource(), "hangingwarmup", NullLogger<SourceWarmUpTests>.Instance, TimeSpan.FromMilliseconds(100));

        // If SourceWarmUp's own timeout regressed back to an unbounded await, `bound` never
        // completes — fail this test within a few seconds instead of hanging the whole suite.
        var completed = await Task.WhenAny(bound, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(bound, completed);
        await bound; // rethrows if RunAsync itself failed instead of just logging
    }

    [Fact]
    public async Task A_hanging_WarmUpAsync_is_logged_as_a_timeout()
    {
        var log = new CapturingLogger<SourceWarmUpTests>();

        var bound = SourceWarmUp.RunAsync(new HangingWarmUpSource(), "hangingwarmup", log, TimeSpan.FromMilliseconds(100));

        // Same bounded-wait reasoning as the test above: don't let a regressed timeout hang
        // this test (and the whole run) forever — fail fast instead.
        var completed = await Task.WhenAny(bound, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(bound, completed);
        await bound;

        Assert.Contains(log.Messages, m => m.Contains("hangingwarmup", StringComparison.Ordinal));
    }
}
