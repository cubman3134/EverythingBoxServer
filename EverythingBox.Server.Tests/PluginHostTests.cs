using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

file sealed class StubContext : IPluginContext
{
    public ILoggerFactory Loggers => NullLoggerFactory.Instance;
    public HttpClient Http { get; } = new();
    public string CacheDirectory => Path.GetTempPath();
    public T? GetConfig<T>() where T : class => null;
}

public class PluginHostTests
{
    private static string PluginsRoot(string name) =>
        Path.Combine(AppContext.BaseDirectory, "testplugins", name);

    private static PluginHost NewHost() => new(NullLogger<PluginHost>.Instance);

    /// <summary>Copies one staged fixture into a fresh directory so each test sees
    /// only the plugin it cares about.</summary>
    private static string IsolatedRoot(params string[] fixtures)
    {
        var root = Path.Combine(Path.GetTempPath(), "ebs-plugintest-" + Guid.NewGuid().ToString("N"));
        foreach (var fixture in fixtures)
        {
            var dest = Path.Combine(root, fixture);
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(PluginsRoot(fixture)))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }
        return root;
    }

    [Fact]
    public void Loads_a_plugin_and_its_sources()
    {
        var loaded = NewHost().Load(IsolatedRoot("good"), _ => new StubContext());

        var plugin = Assert.Single(loaded);
        Assert.Equal("good", plugin.Key);
        Assert.Equal("Good Test Plugin", plugin.DisplayName);
        Assert.Single(plugin.Sources);
    }

    /// <summary>The one that matters: a source built in a plugin assembly must satisfy
    /// the host's IMediaSource, which only holds if Abstractions was NOT reloaded
    /// inside the plugin's load context.</summary>
    [Fact]
    public async Task Plugin_source_is_type_identical_across_the_load_context()
    {
        var loaded = NewHost().Load(IsolatedRoot("good"), _ => new StubContext());

        IMediaSource source = loaded.Single().Sources.Single();
        Assert.Equal("good", source.Key);

        var catalog = await source.SearchAsync("all", null, new SourceContext(), CancellationToken.None);
        Assert.Equal("One", catalog.Items.Single().Title);
    }

    [Fact]
    public void Skips_a_plugin_whose_api_version_is_incompatible_and_one_that_throws()
    {
        // The bad fixture holds both; neither survives, and Load still returns.
        var loaded = NewHost().Load(IsolatedRoot("bad"), _ => new StubContext());
        Assert.Empty(loaded);
    }

    [Fact]
    public void One_bad_plugin_does_not_stop_a_good_one()
    {
        var loaded = NewHost().Load(IsolatedRoot("good", "bad"), _ => new StubContext());
        Assert.Equal("good", Assert.Single(loaded).Key);
    }

    [Fact]
    public void Missing_plugins_directory_yields_nothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ebs-no-such-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(NewHost().Load(missing, _ => new StubContext()));
    }

    [Fact]
    public void A_directory_with_no_plugin_assembly_yields_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "ebs-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nothing-here"));
        File.WriteAllText(Path.Combine(root, "nothing-here", "readme.txt"), "not a plugin");

        Assert.Empty(NewHost().Load(root, _ => new StubContext()));
    }
}
