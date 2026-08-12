using System.Net;
using System.Text.Json;

namespace EverythingBox.Server.Tests;

/// <summary>Integration tests over the /{token}/sync object-store routes, driving the real host
/// booted with Sync enabled (small quota) via <see cref="SyncServerFactory"/>. Covers token-prefix
/// auth, the put→get byte round-trip (ETag + X-Sync-Meta), the list/tombstone view, the If-Match /
/// If-None-Match:* compare-and-swap, the per-namespace quota (507) and per-object cap (400), and
/// namespace validation (400).</summary>
[Collection(SyncServerCollection.Name)]
public class SyncEndpointsTests
{
    private readonly SyncServerFactory _factory;
    public SyncEndpointsTests(SyncServerFactory factory) => _factory = factory;

    private static string Base => "/" + SyncServerFactory.Token;

    private sealed record ListResp(List<ListItem> Objects);
    private sealed record ListItem(string Key, string Version, string? Meta, long Size, bool Deleted);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static async Task<ListResp> ReadListAsync(HttpResponseMessage r)
        => (await JsonSerializer.DeserializeAsync<ListResp>(await r.Content.ReadAsStreamAsync(), JsonOpts))!;

    // ---- auth ----

    [Fact]
    public async Task Sync_route_without_the_token_prefix_does_not_route()
    {
        var response = await _factory.CreateClient().GetAsync("/sync/authns");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sync_list_under_the_token_prefix_succeeds()
    {
        var response = await _factory.CreateClient().GetAsync($"{Base}/sync/authns");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- put -> get byte round-trip ----

    [Fact]
    public async Task Put_then_get_round_trips_bytes_etag_and_meta()
    {
        var client = _factory.CreateClient();
        var bytes = new byte[] { 1, 2, 3, 4, 250, 0, 128, 42 };
        const string url = "/sync/roundtrip/resume%2Fabc";

        var put = new HttpRequestMessage(HttpMethod.Put, Base + url) { Content = new ByteArrayContent(bytes) };
        put.Headers.TryAddWithoutValidation("X-Sync-Meta", "meta-blob-123");
        var putResp = await client.SendAsync(put);

        Assert.Equal(HttpStatusCode.NoContent, putResp.StatusCode);
        var etag = putResp.Headers.ETag!.Tag; // includes surrounding quotes
        Assert.False(string.IsNullOrEmpty(etag));

        var getResp = await client.GetAsync(Base + url);
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.Equal(bytes, await getResp.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, getResp.Headers.ETag!.Tag);
        Assert.Equal("meta-blob-123", getResp.Headers.GetValues("X-Sync-Meta").Single());
    }

    // ---- list + tombstone ----

    [Fact]
    public async Task List_shows_both_keys_with_sizes_and_versions_and_a_deleted_key_as_tombstone()
    {
        var client = _factory.CreateClient();
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 9, 9, 9, 9, 9 };

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Base}/sync/listns/a", new ByteArrayContent(a))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Base}/sync/listns/b", new ByteArrayContent(b))).StatusCode);

        var list = await ReadListAsync(await client.GetAsync($"{Base}/sync/listns"));
        var ia = list.Objects.Single(o => o.Key == "a");
        var ib = list.Objects.Single(o => o.Key == "b");
        Assert.Equal(a.Length, ia.Size);
        Assert.Equal(b.Length, ib.Size);
        Assert.False(ia.Deleted);
        Assert.False(ib.Deleted);
        Assert.False(string.IsNullOrEmpty(ia.Version));
        Assert.False(string.IsNullOrEmpty(ib.Version));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Base}/sync/listns/b")).StatusCode);

        var afterDelete = await ReadListAsync(await client.GetAsync($"{Base}/sync/listns"));
        Assert.True(afterDelete.Objects.Single(o => o.Key == "b").Deleted);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/sync/listns/b")).StatusCode);
    }

    // ---- If-Match compare-and-swap ----

    [Fact]
    public async Task If_Match_stale_is_rejected_and_current_succeeds()
    {
        var client = _factory.CreateClient();
        const string url = "/sync/casmatch/k";

        var first = await client.PutAsync(Base + url, new ByteArrayContent(new byte[] { 1 }));
        var current = first.Headers.ETag!.Tag;

        var stale = new HttpRequestMessage(HttpMethod.Put, Base + url) { Content = new ByteArrayContent(new byte[] { 2 }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"deadbeefdeadbeefdeadbeefdeadbeef\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);

        var good = new HttpRequestMessage(HttpMethod.Put, Base + url) { Content = new ByteArrayContent(new byte[] { 3 }) };
        good.Headers.TryAddWithoutValidation("If-Match", current);
        var goodResp = await client.SendAsync(good);
        Assert.Equal(HttpStatusCode.NoContent, goodResp.StatusCode);
        Assert.NotEqual(current, goodResp.Headers.ETag!.Tag);
    }

    // ---- If-None-Match:* create-only ----

    [Fact]
    public async Task If_None_Match_star_rejects_a_live_key_and_creates_a_fresh_one()
    {
        var client = _factory.CreateClient();

        var fresh = new HttpRequestMessage(HttpMethod.Put, $"{Base}/sync/casnone/new") { Content = new ByteArrayContent(new byte[] { 7 }) };
        fresh.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(fresh)).StatusCode);

        var again = new HttpRequestMessage(HttpMethod.Put, $"{Base}/sync/casnone/new") { Content = new ByteArrayContent(new byte[] { 8 }) };
        again.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(again)).StatusCode);
    }

    // ---- quota (507) + per-object cap (400) ----

    [Fact]
    public async Task Exceeding_the_namespace_quota_is_507()
    {
        var client = _factory.CreateClient();
        var chunk = new byte[32768]; // == MaxObjectBytes; two fill the 65536 quota exactly

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Base}/sync/quota/a", new ByteArrayContent(chunk))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Base}/sync/quota/b", new ByteArrayContent(chunk))).StatusCode);

        var third = await client.PutAsync($"{Base}/sync/quota/c", new ByteArrayContent(chunk));
        Assert.Equal(HttpStatusCode.InsufficientStorage, third.StatusCode);
    }

    [Fact]
    public async Task A_single_body_over_the_object_cap_is_400()
    {
        var client = _factory.CreateClient();
        var tooBig = new byte[32769]; // one over MaxObjectBytes
        var resp = await client.PutAsync($"{Base}/sync/toobig/k", new ByteArrayContent(tooBig));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- namespace validation ----

    [Fact]
    public async Task An_invalid_namespace_segment_is_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PutAsync($"{Base}/sync/bad!ns/k", new ByteArrayContent(new byte[] { 1 }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
