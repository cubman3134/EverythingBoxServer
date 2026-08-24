using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EverythingBox.Server.Tests;

/// <summary>
/// The homebrew endpoint's best-effort contract, asserted over the real host rather than over the
/// endpoint delegate — the interesting failures (a plugin-less server 404ing the route, one bad
/// source taking the whole page down with it) only exist once routing and DI are in play.
///
/// This is deliberately more coverage than the romhack surface it is modelled on has: nothing there
/// proves a stock server answers those routes sanely, and that is exactly the property most likely
/// to rot unnoticed, because a server with no plugin is the shape almost nobody tests against.
///
/// Joins <see cref="AddonServerCollection"/> for the reason every class touching the EBS_*
/// environment variables does — see that collection's doc comment. Like <see cref="StockServerTests"/>
/// it builds its own <see cref="StockServerFactory"/> rather than injecting the shared fixture: a
/// no-plugin config is a different config and must not share a host instance with one.
/// </summary>
[Collection(AddonServerCollection.Name)]
public class HomebrewEndpointTests : IDisposable
{
    // Disposing the stock factory also disposes anything WithWebHostBuilder derived from it, so this
    // one field cleans up both hosts and the temp tree underneath them.
    private readonly StockServerFactory _stock = new();   // writes a config with Indexers: [], no plugin

    public void Dispose() => _stock.Dispose();

    [Fact]
    public async Task With_no_plugin_at_all_a_system_answers_200_and_an_empty_page()
    {
        // The whole best-effort contract in one assertion. "No homebrew for this console" and "the
        // source is down" look the same to someone browsing, and a stock server has no source at all
        // — so this must be 200 with nothing in it, never 404 (which the client would have to render
        // as a dead end) and never 500.
        var client = _stock.CreateClient();

        var response = await client.GetAsync("/homebrew/nds");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("items").EnumerateArray());

        // And no cursor: there is no next page of nothing to follow.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task One_misbehaving_source_does_not_cost_the_others_their_rows()
    {
        // The rule most likely to regress, because the obvious refactor — one try around the whole
        // loop — still passes every single-source test. A source that throws must cost only its own
        // rows, and the request must still be a 200.
        var client = Sourced(
            new ThrowingSource(),
            new FakeSource(new HomebrewListing([Title("hb:1", "Something Playable")], NextCursor: null)));

        var response = await client.GetAsync("/homebrew/nds");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var only = Assert.Single(body.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal("hb:1", only.GetProperty("id").GetString());
        Assert.Equal("Something Playable", only.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_cursor_round_trips_untouched_in_both_directions()
    {
        // The cursor is opaque: what a source returns is what the body carries, and what a caller
        // sends is what the source is asked with. This server parses neither, and a test that only
        // checked the outbound half would miss a server that quietly normalised the inbound one.
        var source = new FakeSource(new HomebrewListing([Title("hb:1", "First")], NextCursor: "page=2|opaque"));
        var client = Sourced(source);

        var body = await client.GetFromJsonAsync<JsonElement>("/homebrew/gba");
        Assert.Equal("page=2|opaque", body.GetProperty("nextCursor").GetString());

        await client.GetFromJsonAsync<JsonElement>("/homebrew/gba?cursor=page%3D2%7Copaque");
        Assert.Equal("gba", source.LastSystemId);
        Assert.Equal("page=2|opaque", source.LastCursor);
    }

    [Fact]
    public async Task A_source_with_nothing_for_a_system_is_an_empty_page_not_an_error()
    {
        // A source that knows nothing about a console returns an empty page rather than throwing, and
        // the endpoint hands that straight back — indistinguishable from the stock case above, which
        // is exactly the point: the client renders one empty level either way.
        var client = Sourced(new FakeSource(new HomebrewListing([], NextCursor: null)));

        var response = await client.GetAsync("/homebrew/nonsense");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    /// <summary>A client onto the stock host with the sources registration replaced, so a test can
    /// supply sources without a plugin on disk to load them from. The last registration wins, which
    /// is what makes this an override of Program.cs's own rather than an addition beside it.</summary>
    private HttpClient Sourced(params IHomebrewSource[] sources) =>
        _stock.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                   services.AddSingleton<IReadOnlyList<IHomebrewSource>>(sources)))
              .CreateClient();

    private static HomebrewTitle Title(string id, string title) =>
        new(id, title, Author: null, Version: null, Description: null, ImageUrl: null);

    private sealed class FakeSource(HomebrewListing page) : IHomebrewSource
    {
        public string? LastSystemId { get; private set; }
        public string? LastCursor { get; private set; }

        public Task<HomebrewListing> ListAsync(string systemId, string? cursor, CancellationToken ct)
        {
            LastSystemId = systemId;
            LastCursor = cursor;
            return Task.FromResult(page);
        }
    }

    private sealed class ThrowingSource : IHomebrewSource
    {
        public Task<HomebrewListing> ListAsync(string systemId, string? cursor, CancellationToken ct)
            => throw new InvalidOperationException("this source is having a bad day");
    }
}
