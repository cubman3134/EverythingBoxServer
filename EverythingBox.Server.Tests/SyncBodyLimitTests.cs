using System.Net;
using EverythingBox.Server;
using EverythingBox.Server.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Proves FIX I1: the sync PUT handler raises Kestrel's per-request body limit to the store's
/// <c>MaxObjectBytes</c>, so a large savestate is counted against the real cap instead of being
/// pre-empted by Kestrel's small default.
///
/// This runs against a REAL in-process Kestrel host, NOT a <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>.
/// TestServer (what a WebApplicationFactory uses) does not enforce Kestrel's
/// <c>MaxRequestBodySize</c> at all, so a WAF-based body-limit test passes identically whether or
/// not the handler raises the limit — it cannot prove the fix. A genuine Kestrel transport does
/// enforce it, and honours a mid-request raise via <c>IHttpMaxRequestBodySizeFeature</c>, which is
/// exactly the production path. The host is bound to 127.0.0.1:0 (an ephemeral port) and maps the
/// real <see cref="SyncEndpoints.MapSync"/> over a real <see cref="SyncStore"/>.
/// </summary>
public class SyncBodyLimitTests : IAsyncLifetime
{
    private const long GlobalKestrelLimit = 4096;  // tiny global limit the handler must raise past
    private const long MaxObjectBytes = 1048576;   // 1 MiB — the store's real per-object cap

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-bodylimit-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The small GLOBAL Kestrel limit that, without the handler's per-request raise, would 413 any
        // body over 4096 bytes before the store could count it against MaxObjectBytes.
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GlobalKestrelLimit);
        builder.Services.AddSingleton(new SyncStore(_root, perNamespaceQuotaBytes: 104857600, maxObjectBytes: MaxObjectBytes));

        _app = builder.Build();
        _app.MapSync(""); // routes at /sync/{ns}/{**key}
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A body larger than the 4096 global Kestrel limit but under MaxObjectBytes must succeed: the
    // PUT handler's per-request raise to MaxObjectBytes beat the global limit. Without the raise
    // Kestrel would 413 (RequestEntityTooLarge) before the handler ran.
    [Fact]
    public async Task Body_over_the_global_limit_but_under_the_object_cap_is_204()
    {
        var body = new byte[20000]; // > 4096 global limit, < 1 MiB object cap
        var resp = await _client.PutAsync("/sync/bodylim/k", new ByteArrayContent(body));
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // The raise does not remove the ceiling: it lifts the per-request limit to EXACTLY
    // MaxObjectBytes, so a body over that is still rejected — under a real Kestrel transport with a
    // Content-Length, Kestrel enforces the raised limit itself and returns 413 before the store
    // streams the body. (The store's own CopyCappedAsync → 400 mapping is a backstop, covered
    // directly by SyncEndpointsTests.A_single_body_over_the_object_cap_is_400, whose TestServer host
    // does not enforce the Kestrel limit and so reaches the store.) Either way an oversize body is
    // refused with a 4xx; here the meaningful assertion is that the ceiling is still at MaxObjectBytes.
    [Fact]
    public async Task Body_over_the_object_cap_is_still_rejected()
    {
        var tooBig = new byte[MaxObjectBytes + 1];
        var resp = await _client.PutAsync("/sync/bodylim/big", new ByteArrayContent(tooBig));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
    }
}
