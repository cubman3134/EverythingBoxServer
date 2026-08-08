using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Captures the <see cref="SourceContext"/> handed to whichever member the endpoint
/// under test calls, so a test can inspect exactly what a plugin would see.</summary>
file sealed class CapturingSource : IMediaSource
{
    public string Key => "capturing";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public SourceContext? LastContext { get; private set; }

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        LastContext = ctx;
        return Task.FromResult(SourceCatalog.Empty(""));
    }

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        LastContext = ctx;
        return Task.FromResult(SourceCatalog.Empty(""));
    }

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        LastContext = ctx;
        return Task.FromResult<SourceStream?>(null);
    }
}

/// <summary>
/// Task 1 of milestone 3d1: the host curates and forwards selected request headers to a
/// source via <see cref="SourceContext.RequestHeaders"/>, but never sensitive ones
/// (Authorization, Cookie) — those exist for the HOST's own auth, not a plugin's use.
/// Drives the endpoint methods directly (same pattern as
/// <see cref="AddonEndpointsCancellationTests"/>) rather than over real HTTP, so the
/// captured <see cref="SourceContext"/> can be inspected without a JSON round-trip.
/// </summary>
public class AddonEndpointsRequestHeadersTests
{
    private static DefaultHttpContext RequestWithHeaders()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Test"] = "v";
        http.Request.Headers["Authorization"] = "secret";
        http.Request.Headers["Cookie"] = "c";
        return http;
    }

    [Fact]
    public async Task Catalog_forwards_a_plain_header_but_excludes_sensitive_ones()
    {
        var source = new CapturingSource();
        var router = new SourceRouter([source]);
        var http = RequestWithHeaders();

        await AddonEndpoints.CatalogAsync("capturing:x", null, http, router, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.NotNull(source.LastContext);
        var headers = source.LastContext!.RequestHeaders;
        Assert.NotNull(headers);
        Assert.Equal("v", headers!["x-test"]); // case-insensitive lookup
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.False(headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task Stream_forwards_a_plain_header_but_excludes_sensitive_ones()
    {
        var source = new CapturingSource();
        var router = new SourceRouter([source]);
        var http = RequestWithHeaders();

        await AddonEndpoints.StreamAsync("movie", "capturing:x", null, null, http, router, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.NotNull(source.LastContext);
        var headers = source.LastContext!.RequestHeaders;
        Assert.NotNull(headers);
        Assert.Equal("v", headers!["x-test"]); // case-insensitive lookup
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.False(headers.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task Stream_still_sets_ClientCanCurl_alongside_RequestHeaders()
    {
        var source = new CapturingSource();
        var router = new SourceRouter([source]);
        var http = RequestWithHeaders();

        await AddonEndpoints.StreamAsync("movie", "capturing:x", null, "curl", http, router, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.True(source.LastContext!.ClientCanCurl);
    }

    [Fact]
    public async Task No_headers_yields_a_null_RequestHeaders()
    {
        var source = new CapturingSource();
        var router = new SourceRouter([source]);
        var http = new DefaultHttpContext();

        await AddonEndpoints.CatalogAsync("capturing:x", null, http, router, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Null(source.LastContext!.RequestHeaders);
    }
}
