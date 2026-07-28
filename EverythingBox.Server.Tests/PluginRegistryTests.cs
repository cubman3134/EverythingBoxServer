using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;

namespace EverythingBox.Server.Tests;

public class PluginRegistryTests
{
    [Fact]
    public void Registers_a_source()
    {
        var registry = new PluginRegistry();
        registry.AddSource(new FakeSource("alpha"));
        Assert.Single(registry.Sources);
    }

    [Fact]
    public void Rejects_a_key_containing_a_colon()
    {
        var registry = new PluginRegistry();
        // ':' separates key from payload in every id the server emits, so a key
        // containing one would make routing ambiguous.
        var ex = Assert.Throws<ArgumentException>(() => registry.AddSource(new FakeSource("al:pha")));
        Assert.Contains("':'", ex.Message);
    }

    [Fact]
    public void Rejects_a_blank_key()
    {
        var registry = new PluginRegistry();
        Assert.Throws<ArgumentException>(() => registry.AddSource(new FakeSource("  ")));
    }

    [Fact]
    public void Rejects_a_duplicate_key_case_insensitively()
    {
        var registry = new PluginRegistry();
        registry.AddSource(new FakeSource("alpha"));
        Assert.Throws<InvalidOperationException>(() => registry.AddSource(new FakeSource("ALPHA")));
    }
}
