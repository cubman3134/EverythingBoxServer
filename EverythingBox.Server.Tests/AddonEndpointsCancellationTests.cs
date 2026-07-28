using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Throws OperationCanceledException from every member, carrying the SAME
/// CancellationToken the caller passed in — the shape of a source that legitimately
/// observed the request's own cancellation (e.g. an inner HttpClient call honouring ct),
/// as opposed to <c>OperationCanceledSource</c> in TestPlugin.Throwing, which throws it
/// unconditionally regardless of ct.</summary>
file sealed class GenuinelyCancelingSource : IMediaSource
{
    public string Key => "genuinelycanceling";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => throw new OperationCanceledException("genuinely cancelled", ct);

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => throw new OperationCanceledException("genuinely cancelled", ct);

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => throw new OperationCanceledException("genuinely cancelled", ct);

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => throw new OperationCanceledException("genuinely cancelled", ct);
}

/// <summary>
/// C1's other half, tested directly against AddonEndpoints' internal methods (accessible via
/// InternalsVisibleTo) rather than over real HTTP: HTTP-level cancellation is driven by the
/// CLIENT tearing down its own connection, which races the server's handling of it and can't
/// reliably prove which code path ran. Calling the endpoint logic directly with an
/// already-cancelled CancellationToken removes that race and proves the actual claim: when
/// ct.IsCancellationRequested is true, an OperationCanceledException must propagate out of
/// the endpoint rather than being swallowed into a normal-looking "empty" result — that would
/// hide a genuine client disconnect behind a false success.
///
/// Every test bounds itself with Task.WhenAny/Task.Delay rather than just awaiting the call
/// directly: if the fix under test were reverted to "swallow everything", the call would not
/// hang, but a DIFFERENT accidental regression (e.g. losing the timeout call entirely) should
/// fail this test quickly rather than hang the whole run.
/// </summary>
public class AddonEndpointsCancellationTests
{
    private static async Task AssertPropagatesAsync(Func<Task> call)
    {
        var task = call();
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed);
        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_genuinely_cancelled_catalog_request_propagates_rather_than_being_swallowed()
    {
        var router = new SourceRouter([new GenuinelyCancelingSource()]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await AssertPropagatesAsync(() =>
            AddonEndpoints.CatalogAsync("genuinelycanceling:x", null, router, NullLoggerFactory.Instance, cts.Token));
    }

    [Fact]
    public async Task A_genuinely_cancelled_detail_request_propagates_rather_than_being_swallowed()
    {
        var router = new SourceRouter([new GenuinelyCancelingSource()]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await AssertPropagatesAsync(() =>
            AddonEndpoints.DetailAsync("movie", "genuinelycanceling:x", router, NullLoggerFactory.Instance, cts.Token));
    }

    [Fact]
    public async Task A_genuinely_cancelled_stream_request_propagates_rather_than_being_swallowed()
    {
        var router = new SourceRouter([new GenuinelyCancelingSource()]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await AssertPropagatesAsync(() =>
            AddonEndpoints.StreamAsync("movie", "genuinelycanceling:x", null, null, router, NullLoggerFactory.Instance, cts.Token));
    }

    [Fact]
    public async Task A_genuinely_cancelled_proxy_request_propagates_rather_than_being_swallowed()
    {
        var router = new SourceRouter([new GenuinelyCancelingSource()]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();

        await AssertPropagatesAsync(() =>
            AddonEndpoints.ProxyAsync("genuinelycanceling", "x", "file.bin", http, router, NullLoggerFactory.Instance, cts.Token));
    }
}
