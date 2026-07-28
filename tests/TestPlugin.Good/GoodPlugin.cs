using EverythingBox.Server.Abstractions;

namespace TestPlugin.Good;

public sealed class GoodSource : IMediaSource
{
    public string Key => "good";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [new CatalogDescriptor("all", "All", "movie")];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("All", [new CatalogItem("one", "One", "", "movie")]));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("All"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(new SourceStream($"https://example.test/{itemId}", "video/mp4"));
}

public sealed class GoodPlugin : IPlugin
{
    public string Key => "good";
    public string DisplayName => "Good Test Plugin";
    public Version ApiVersion => ServerApi.Version;

    public void Configure(IPluginRegistry registry, IPluginContext context)
        => registry.AddSource(new GoodSource());
}
