using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Tests;

/// <summary>A distinct tracker instance per test plugin — identity is what "which one won" is asserted on.</summary>
file sealed class FakeTracker : IProviderPerformanceTracker
{
    public IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> providers) => providers;
    public void Record(IReadOnlyList<ProviderOutcome> outcomes) { }
}

/// <summary>Captures every log entry's level and rendered message.</summary>
file sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

public class ProviderTrackerReconcilerTests
{
    private static LoadedPlugin Plugin(string key, IProviderPerformanceTracker? tracker)
        => new(key, key, [], [], [], tracker);

    [Fact]
    public void The_first_plugin_in_load_order_wins_when_several_register_a_tracker()
    {
        var first = new FakeTracker();
        var second = new FakeTracker();
        var log = new CapturingLogger();

        var winner = ProviderTrackerReconciler.Resolve(
            [Plugin("alpha", first), Plugin("beta", second)], log);

        Assert.Same(first, winner); // identity: the FIRST plugin's tracker, not the second's
    }

    [Fact]
    public void A_warning_names_every_registering_plugin_and_the_winner()
    {
        var log = new CapturingLogger();

        ProviderTrackerReconciler.Resolve(
            [Plugin("alpha", new FakeTracker()), Plugin("beta", new FakeTracker())], log);

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("alpha", warning.Message);
        Assert.Contains("beta", warning.Message);
        Assert.Contains("alpha", warning.Message); // winner named; alpha is both listed and the winner
    }

    [Fact]
    public void A_single_registered_tracker_is_used_with_no_warning()
    {
        var only = new FakeTracker();
        var log = new CapturingLogger();

        var winner = ProviderTrackerReconciler.Resolve(
            [Plugin("alpha", null), Plugin("beta", only)], log); // alpha has none → beta is the sole tracker

        Assert.Same(only, winner);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void No_registered_tracker_yields_null_and_no_warning()
    {
        var log = new CapturingLogger();

        var winner = ProviderTrackerReconciler.Resolve([Plugin("alpha", null)], log);

        Assert.Null(winner);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }
}
