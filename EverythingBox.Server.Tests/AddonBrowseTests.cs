using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EverythingBox.Server.Tests;

/// <summary>Drives the real host over HTTP with the "good" fixture plugin installed.</summary>
[Collection(AddonServerCollection.Name)]
public class AddonBrowseTests
{
    private readonly PluginServerFactory _factory;
    public AddonBrowseTests(PluginServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Manifest_lists_the_plugins_catalog()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        Assert.Equal("media-source", json.GetProperty("type").GetString());
        var catalog = json.GetProperty("catalogs").EnumerateArray().Single();
        Assert.Equal("good:all", catalog.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Catalog_returns_the_sources_items()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/catalog/good:all.json");

        var item = json.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("good:one", item.GetProperty("id").GetString());   // prefixed on the way out
        Assert.Equal("One", item.GetProperty("title").GetString());
        Assert.False(json.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task Catalog_search_extra_is_honoured()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/good:all/search=one.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_catalog_is_an_empty_catalog_not_an_error()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/nosuch:thing.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Health_is_ok()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/health");
        Assert.True(json.GetProperty("ok").GetBoolean());
    }
}
