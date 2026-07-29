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

file sealed class FakeMetadata(string name) : IMetadataSource
{
    public string Name => name;
    public IReadOnlyList<string> SupportedMediaTypes { get; } = ["movie"];
    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>([]);
}

file sealed class ThrowingNameMetadata : IMetadataSource
{
    public string Name => throw new InvalidOperationException("name boom");
    public IReadOnlyList<string> SupportedMediaTypes => throw new InvalidOperationException("types boom");
    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>([]);
}

file sealed class FakeTracker : IProviderPerformanceTracker
{
    public IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> providers) => providers;
    public void Record(IReadOnlyList<ProviderOutcome> outcomes) { }
}

file sealed class ThrowingTracker : IProviderPerformanceTracker
{
    public IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> providers)
        => throw new InvalidOperationException("prioritize boom");
    public void Record(IReadOnlyList<ProviderOutcome> outcomes)
        => throw new InvalidOperationException("record boom");
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

    [Fact]
    public void Registers_a_metadata_source()
    {
        var registry = new PluginRegistry();
        registry.AddMetadata(new FakeMetadata("alpha"));
        Assert.Single(registry.MetadataSources);
    }

    [Fact]
    public void Rejects_a_null_metadata_source()
        => Assert.Throws<ArgumentNullException>(() => new PluginRegistry().AddMetadata(null!));

    [Fact]
    public void A_metadata_source_whose_members_throw_does_not_escape_registration()
    {
        // Name and SupportedMediaTypes are plugin code. Registration must not read them.
        var registry = new PluginRegistry();
        Assert.Null(Record.Exception(() => registry.AddMetadata(new ThrowingNameMetadata())));
    }

    [Fact]
    public void Metadata_indexers_and_sources_are_three_separate_registrations()
    {
        var registry = new PluginRegistry();
        registry.AddSource(new FakeSource("alpha"));
        registry.AddIndexer(new FakeIndexer("alpha"));
        registry.AddMetadata(new FakeMetadata("alpha"));

        Assert.Single(registry.Sources);
        Assert.Single(registry.Indexers);
        Assert.Single(registry.MetadataSources);
    }

    [Fact]
    public void A_plugin_can_supply_a_provider_tracker()
    {
        var registry = new PluginRegistry();
        registry.AddProviderTracker(new FakeTracker());
        Assert.NotNull(registry.ProviderTracker);
    }

    [Fact]
    public void Rejects_a_null_provider_tracker()
        => Assert.Throws<ArgumentNullException>(() => new PluginRegistry().AddProviderTracker(null!));

    [Fact]
    public void Registering_a_tracker_does_not_invoke_it()
    {
        // Its methods are plugin code. Registration must not call them.
        var registry = new PluginRegistry();
        Assert.Null(Record.Exception(() => registry.AddProviderTracker(new ThrowingTracker())));
    }

    [Fact]
    public void A_second_provider_tracker_registration_throws()
    {
        // Unlike sources and indexers, only one tracker can order the provider list.
        // Two plugins both supplying one is a real configuration mistake, so this is
        // a throw (same as AddSource's duplicate-key check) rather than a silent
        // replace — a silent replace would leave whoever registered the first tracker
        // wondering, with no error anywhere, why it's never consulted.
        var registry = new PluginRegistry();
        registry.AddProviderTracker(new FakeTracker());

        var ex = Assert.Throws<InvalidOperationException>(() => registry.AddProviderTracker(new FakeTracker()));
        Assert.Contains("already registered", ex.Message);

        // And the first registration is left untouched.
        Assert.NotNull(registry.ProviderTracker);
    }

    [Fact]
    public void The_nested_archive_reader_contract_is_not_shipped()
    {
        // Removed because nothing implemented or consumed it. It belongs in the plan
        // that needs it, not in the contract ahead of time.
        var abstractions = typeof(IPluginRegistry).Assembly;
        Assert.DoesNotContain(abstractions.GetExportedTypes(), t => t.Name == "INestedArchiveReader");
    }
}
