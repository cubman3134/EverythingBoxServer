using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;

namespace EverythingBox.Server.Tests;

file sealed class FakeIndexer(string name) : ITorrentProvider
{
    public string Name => name;
    public ProviderCapabilities Capabilities { get; } = new() { SupportedMediaTypes = new HashSet<MediaType>() };

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TorrentResult>>([]);
}

file sealed class ThrowingNameIndexer : ITorrentProvider
{
    public string Name => throw new InvalidOperationException("name boom");
    public ProviderCapabilities Capabilities { get; } = new() { SupportedMediaTypes = new HashSet<MediaType>() };

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TorrentResult>>([]);
}

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

    [Fact]
    public void Registers_an_indexer()
    {
        var registry = new PluginRegistry();
        registry.AddIndexer(new FakeIndexer("alpha"));
        Assert.Single(registry.Indexers);
    }

    [Fact]
    public void Indexers_and_sources_are_separate_registrations()
    {
        var registry = new PluginRegistry();
        registry.AddIndexer(new FakeIndexer("alpha"));
        registry.AddSource(new FakeSource("alpha"));

        // The same key on both tiers is fine — they are different registries.
        Assert.Single(registry.Indexers);
        Assert.Single(registry.Sources);
    }

    [Fact]
    public void Rejects_a_null_indexer()
        => Assert.Throws<ArgumentNullException>(() => new PluginRegistry().AddIndexer(null!));

    [Fact]
    public void An_indexer_whose_Name_throws_does_not_escape_registration()
    {
        // Name is plugin-authored code. A throwing getter must not take the load down.
        var registry = new PluginRegistry();
        var ex = Record.Exception(() => registry.AddIndexer(new ThrowingNameIndexer()));
        Assert.Null(ex);
    }

    [Fact]
    public void Two_indexers_may_share_a_name()
    {
        // Unlike source keys, indexer names are labels, not routing keys — a user may
        // legitimately configure two endpoints of the same kind.
        var registry = new PluginRegistry();
        registry.AddIndexer(new FakeIndexer("same"));
        registry.AddIndexer(new FakeIndexer("same"));
        Assert.Equal(2, registry.Indexers.Count);
    }
}
