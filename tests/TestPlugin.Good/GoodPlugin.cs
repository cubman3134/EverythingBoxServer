using EverythingBox.Server.Abstractions;

namespace TestPlugin.Good;

public sealed class GoodSource(string? warmUpMarkerPath = null) : IMediaSource
{
    public string Key => "good";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [new CatalogDescriptor("all", "All", "movie")];

    // F6: proves the host actually calls WarmUpAsync at startup — writes a marker file a
    // test can observe from outside this plugin's own AssemblyLoadContext.
    public Task<WarmUpResult> WarmUpAsync(CancellationToken ct)
    {
        if (warmUpMarkerPath is not null) File.WriteAllText(warmUpMarkerPath, "warmed");
        return Task.FromResult(WarmUpResult.Ready);
    }

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        // Title echoes the query so a test can assert exactly what arrived.
        => Task.FromResult(new SourceCatalog(query ?? "All", [new CatalogItem("one", "One", "", "movie")]));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("All"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(itemId switch
        {
            "notice"   => SourceStream.FromNotice("caching, retry shortly"),
            "unsafe"   => new SourceStream("magnet:?xt=urn:btih:abc", "video/mp4"),
            "curl"     => new SourceStream("https://example.test/gated.7z", "application/x-7z-compressed", Curl: true),
            "missing"  => null,
            "indexed"  => new SourceStream($"https://example.test/pick-{index}.mkv", "video/x-matroska"),
            _          => new SourceStream($"https://example.test/{itemId}.mkv", "video/x-matroska"),
        });

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
    {
        if (itemId != "proxied") return Task.FromResult<ProxyResponse?>(null);

        var bytes = "PROXIED-BODY"u8.ToArray();
        return Task.FromResult<ProxyResponse?>(new ProxyResponse(new MemoryStream(bytes), "application/octet-stream")
        {
            ContentLength = bytes.Length,
            AcceptRanges = "bytes",
            // Set ONLY when a range arrived, so its presence proves the header reached
            // the source. Must be a valid Content-Range or HttpClient drops it.
            ContentRange = rangeHeader is null ? null : "bytes 0-3/12",
        });
    }
}

public sealed class GoodPlugin : IPlugin
{
    public string Key => "good";
    public string DisplayName => "Good Test Plugin";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
        => registry.AddSource(new GoodSource(Path.Combine(context.CacheDirectory, "warmup.marker")));
}
