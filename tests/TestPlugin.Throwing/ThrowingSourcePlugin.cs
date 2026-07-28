using EverythingBox.Server.Abstractions;

namespace TestPlugin.Throwing;

/// <summary>
/// Loads successfully (unlike the fixtures in TestPlugin.Bad, which are deliberately
/// unloadable) but every member of its one IMediaSource throws once the server is
/// actually driving a request through it. Exists to prove request-time containment
/// (F1): a misbehaving source must degrade to its own empty/absent result, never take
/// down a request for every other installed source.
/// </summary>
public sealed class ThrowingSource : IMediaSource
{
    public string Key => "throwing";

    // Reading this must not break /manifest.json for any other, healthy source.
    public IReadOnlyList<CatalogDescriptor> Catalogs => throw new InvalidOperationException("Catalogs boom");

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("SearchAsync boom");

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("DetailAsync boom");

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("ResolveAsync boom");

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => throw new InvalidOperationException("OpenAsync boom");
}

/// <summary>
/// Returns null where the interface's static type says it can't — a plugin author's
/// nullable annotations are not enforced at runtime, and the host must treat a null
/// SourceCatalog or a null Items list as "nothing found", not a crash.
/// </summary>
public sealed class NullishSource : IMediaSource
{
    public string Key => "nullish";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceCatalog>(null!);

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", null!));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

public sealed class ThrowingSourcePlugin : IPlugin
{
    public string Key => "throwing";
    public string DisplayName => "Throwing Source Plugin";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        registry.AddSource(new ThrowingSource());
        registry.AddSource(new NullishSource());
    }
}
