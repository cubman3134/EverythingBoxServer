using System.Text.Json;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class MetaRouteTests
{
    private sealed class RichSource : IMediaSource
    {
        public string Key => "rich";
        public IReadOnlyList<CatalogDescriptor> Catalogs => [];
        public Task<SourceCatalog> SearchAsync(string c, string? q, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceCatalog> DetailAsync(string i, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceStream?> ResolveAsync(string i, int n, SourceContext x, CancellationToken t) => Task.FromResult<SourceStream?>(null);
        public Task<SourceDetail?> MetaAsync(string i, SourceContext x, CancellationToken t) =>
            Task.FromResult<SourceDetail?>(new SourceDetail("Open Skies", "2024", "The synopsis.", "proxy/rich/abc/p.jpg",
                [new MetaFact("Year", "2024"), new MetaFact("Empty", "")]));
    }

    private sealed class BlankSource : IMediaSource
    {
        public string Key => "blank";
        public IReadOnlyList<CatalogDescriptor> Catalogs => [];
        public Task<SourceCatalog> SearchAsync(string c, string? q, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceCatalog> DetailAsync(string i, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceStream?> ResolveAsync(string i, int n, SourceContext x, CancellationToken t) => Task.FromResult<SourceStream?>(null);
        // no MetaAsync override → default null
    }

    private static string Serialize(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value!);

    [Fact]
    public async Task Meta_route_emits_the_flat_detail_shape_for_a_rich_source()
    {
        var router = new SourceRouter([new RichSource()]);
        var result = await AddonEndpoints.MetaAsync("movie", "rich:abc", router, NullLoggerFactory.Instance, default);
        var json = Serialize(result);
        Assert.Contains("\"title\":\"Open Skies\"", json);
        Assert.Contains("\"overview\":\"The synopsis.\"", json);
        Assert.Contains("\"image\":\"proxy/rich/abc/p.jpg\"", json);
        Assert.Contains("\"label\":\"Year\"", json);
        Assert.Contains("\"value\":\"2024\"", json);
        Assert.DoesNotContain("\"Empty\"", json); // empty-value fact dropped
    }

    [Fact]
    public async Task Meta_route_returns_empty_object_for_a_source_without_MetaAsync()
    {
        var router = new SourceRouter([new BlankSource()]);
        var result = await AddonEndpoints.MetaAsync("movie", "blank:xyz", router, NullLoggerFactory.Instance, default);
        Assert.Equal("{}", Serialize(result));
    }
}
