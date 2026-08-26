using System.Net;
using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EverythingBox.Server.Tests;

/// <summary>
/// The staged-file route, asserted over the real host rather than over <see cref="SafeLocalFileServer"/>
/// directly. Exercising the file server on its own would prove the file server's properties — which
/// <see cref="SafeLocalFileServerTests"/> already does — and prove nothing at all about whether the
/// route goes THROUGH it. The interesting failure is a route that decodes the id and opens the path
/// itself; only a request that reaches the real routing and DI can see that.
///
/// Joins <see cref="AddonServerCollection"/> for the reason every class touching the EBS_*
/// environment variables does — see that collection's doc comment. Like <see cref="HomebrewEndpointTests"/>
/// it builds its own <see cref="StockServerFactory"/> rather than injecting the shared fixture: a
/// no-plugin config is a different config and must not share a host instance with one.
/// </summary>
[Collection(AddonServerCollection.Name)]
public class RomhackFileRouteTests : IDisposable
{
    private readonly StockServerFactory _stock = new();   // writes a config with Indexers: [], no plugin

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rfr-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly RomhackStaging _staging;

    public RomhackFileRouteTests() => _staging = new RomhackStaging(_root, TimeSpan.FromHours(6));

    public void Dispose()
    {
        _stock.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_staged_file_is_served_by_its_id()
    {
        var id = await StageAsync("hack.ips", [1, 2, 3, 4]);

        var response = await Client().GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        // Advertised so the client's download manager knows it can resume — the property the
        // embedded-bytes design could not have at all.
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
    }

    [Fact]
    public async Task A_file_outside_the_staging_root_is_refused()
    {
        // First: the route is live and serving, so the 404 below is a refusal rather than a
        // missing route answering 404 for everything.
        var staged = await StageAsync("hack.ips", [1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync(Url(staged))).StatusCode);

        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(outside, [9, 9, 9, 9]);
        try
        {
            // A real, readable file — the id is well-formed and the path exists. The only thing
            // wrong with it is that it is not inside the staging root, which is precisely what the
            // containment check in SafeLocalFileServer is for. A route that resolved the decoded id
            // to a path itself would serve this happily.
            var response = await Client().GetAsync(Url(SafeLocalFileServer.EncodeId(outside)));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        finally { try { File.Delete(outside); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task A_swept_file_is_gone_rather_than_empty()
    {
        // Reachable by design, not an edge case: retention is age-based because nothing in the
        // protocol reports a successful install, so a client that waits past the window asks for a
        // file that is no longer there. It must get a clean 404, never a 200 with no bytes in it —
        // an empty success would be installed as an empty ROM.
        var dir = _staging.NewFetchDirectory();
        var file = Path.Combine(dir, "hack.ips");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        var id = SafeLocalFileServer.EncodeId(file);

        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-2));
        Assert.Equal(1, _staging.Sweep(DateTimeOffset.UtcNow));

        var response = await Client().GetAsync(Url(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_range_request_gets_only_the_bytes_it_asked_for()
    {
        var body = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var id = await StageAsync("hack.bin", body);

        var request = new HttpRequestMessage(HttpMethod.Get, Url(id));
        request.Headers.Add("Range", "bytes=0-15");
        var response = await Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(body[..16], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 0-15/64", response.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task An_id_that_is_not_an_id_at_all_is_a_404_rather_than_a_failure()
    {
        // The id arrives from a client, so "not decodable" is an ordinary input, not an incident.
        var response = await Client().GetAsync(Url("!!!not-base64!!!"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Writes a file into a fresh fetch directory and returns the id it is served by.</summary>
    private async Task<string> StageAsync(string name, byte[] bytes)
    {
        var file = Path.Combine(_staging.NewFetchDirectory(), name);
        await File.WriteAllBytesAsync(file, bytes);
        return SafeLocalFileServer.EncodeId(file);
    }

    private static string Url(string id) => "/romhack-file/" + Uri.EscapeDataString(id);

    /// <summary>A client onto the stock host with the staging registration replaced, so the test
    /// owns the root the route serves out of. The last registration wins, which is what makes this
    /// an override of Program.cs's own rather than an addition beside it.</summary>
    private HttpClient Client() =>
        _stock.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                   services.AddSingleton(_staging)))
              .CreateClient();
}
