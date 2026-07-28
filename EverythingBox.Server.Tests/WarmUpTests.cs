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
