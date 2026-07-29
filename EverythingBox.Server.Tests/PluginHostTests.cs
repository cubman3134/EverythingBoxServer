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

    // A null here would fail far from the cause — throwing at the point of access makes
    // a test that unexpectedly reaches for Server fail with a clear diagnosis instead.
    public IServerServices Server =>
        throw new NotSupportedException("This test does not provide server services.");
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

        Assert.True(loaded.Count == 1,
            "Expected the plugin to load. A count of 0 usually means IMediaSource is NOT type-identical " +
            "across the load-context boundary: PluginHost's IsAssignableFrom filter drops the plugin before " +
            "any cast. Check that PluginLoadContext.Load defers the shared assemblies to the default context, " +
            "and that Abstractions.dll is not being copied into the plugin folder (Private=\"false\").");

        var loadedPlugin = loaded.First();
        var sources = loadedPlugin.Sources;
        Assert.True(sources.Count == 1,
            "Expected the plugin to provide one IMediaSource. A count of 0 usually means IMediaSource is NOT type-identical " +
            "across the load-context boundary: PluginHost's IsAssignableFrom filter drops the plugin before " +
            "any cast. Check that PluginLoadContext.Load defers the shared assemblies to the default context, " +
            "and that Abstractions.dll is not being copied into the plugin folder (Private=\"false\").");

        IMediaSource source = sources.First();
        Assert.Equal("good", source.Key);

        var catalog = await source.SearchAsync("all", null, new SourceContext(), CancellationToken.None);
        Assert.Equal("One", catalog.Items.Single().Title);
    }

    [Fact]
    public void Skips_every_bad_plugin_without_crashing_the_load()
    {
        // The bad fixture holds an incompatible-API-version plugin, one that throws
        // while registering, and one whose ApiVersion getter itself throws. None
        // survive, and Load still returns normally.
        var loaded = NewHost().Load(IsolatedRoot("bad"), _ => new StubContext());
        Assert.Empty(loaded);
    }

    [Fact]
    public void Second_plugin_with_a_duplicate_key_is_skipped()
    {
        // Two plugin types in the same fixture assembly declare the same key. Only
        // one may register, and Load must still return normally rather than throw.
        var loaded = NewHost().Load(IsolatedRoot("dup"), _ => new StubContext());

        var plugin = Assert.Single(loaded);
        Assert.Equal("dup", plugin.Key);
    }

    [Fact]
    public void An_unloadable_dll_is_skipped_without_throwing()
    {
        // A .dll that is not a managed assembly at all must hit the BadImageFormatException
        // catch in Instantiate and be skipped, not crash Load.
        var root = Path.Combine(Path.GetTempPath(), "ebs-badimage-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "garbage");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "not-a-real-assembly.dll"), [0x00, 0x01, 0x02, 0x03, 0x04]);

        Assert.Empty(NewHost().Load(root, _ => new StubContext()));
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
