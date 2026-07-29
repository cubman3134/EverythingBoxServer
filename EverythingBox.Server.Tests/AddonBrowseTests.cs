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

    // Every built-in source's key, e.g. IndexerSearchSource's "idx" and
    // MetadataBackedVideoSource's "meta" — both are always registered alongside
    // whatever plugins are installed, and a plugin-focused assertion must exclude
    // every one of them, not just "idx:". Add the next built-in source's key here too,
    // or it will slip through this filter the same way "meta:" originally did.
    private static readonly string[] BuiltInSourceKeys = ["idx", "meta"];

    /// <summary>Manifest catalogs minus the always-on built-in ones (see
    /// <see cref="BuiltInSourceKeys"/>), so a test can assert what the fixture PLUGINS
    /// themselves put there without needing to know how many built-in search catalogs
    /// exist.</summary>
    private static List<JsonElement> PluginCatalogs(JsonElement manifest) =>
        manifest.GetProperty("catalogs").EnumerateArray()
            .Where(c => !BuiltInSourceKeys.Any(key =>
                c.GetProperty("id").GetString()!.StartsWith(key + ":", StringComparison.Ordinal)))
            .ToList();

    [Fact]
    public async Task Manifest_lists_the_plugins_catalog()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        Assert.Equal("media-source", json.GetProperty("type").GetString());
        // The built-in IndexerSearchSource ("idx:*") is always registered alongside
        // whatever plugins are installed, so "good:all" is no longer the only catalog —
        // just the only one this fixture plugin contributes.
        var catalog = PluginCatalogs(json).Single();
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

    // F1: the "throwing" fixture plugin is installed alongside "good" specifically so these
    // tests can prove request-time containment over real HTTP, not just at the unit level.

    [Fact]
    public async Task Manifest_still_lists_the_healthy_plugin_even_though_another_plugins_Catalogs_getter_throws()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        // Same "idx:*" caveat as Manifest_lists_the_plugins_catalog: filter to what the
        // plugins themselves contributed before asserting exclusivity.
        var catalog = Assert.Single(PluginCatalogs(json));
        Assert.Equal("good:all", catalog.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Catalog_from_a_source_whose_SearchAsync_throws_is_empty_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/throwing:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Detail_from_a_source_whose_DetailAsync_throws_is_empty_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/detail/movie/throwing:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Catalog_from_a_source_returning_a_null_SourceCatalog_is_empty_not_a_crash()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/nullish:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Detail_from_a_source_returning_a_catalog_with_null_Items_is_empty_not_a_crash()
    {
        var response = await _factory.CreateClient().GetAsync("/detail/movie/nullish:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    // C1: ToWire used to run OUTSIDE the per-source try, so it enumerated plugin-supplied
    // Items unguarded. A null element (the likelier plugin mistake than a null list) or a
    // list whose enumerator itself throws must be contained the same way SearchAsync/
    // DetailAsync throwing already was — and a null element must be SKIPPED, not just
    // caught, so the source's other, healthy items still come back.

    [Fact]
    public async Task Catalog_with_a_null_item_element_skips_it_but_keeps_the_others()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/nullitem:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = json.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("nullitem:keep", item.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Catalog_whose_items_enumerator_throws_is_empty_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/throwingenum:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    // C2 + I1: source.Key is plugin-authored code, read AGAIN after the async call
    // succeeds to prefix wire ids — and read a third time inside the catch block's log
    // call whenever the primary method throws. Both reads used to sit outside any try.
    // KeyArmableSource/KeyArmableAndMethodsThrowSource behave normally (Key works) at
    // plugin load, SourceRouter construction and warm-up — none of which a test controls
    // the timing of — and only throw once a test arms EBS_TEST_KEY_ARMED around its own
    // request, so these reproduce "worked when the router was built, throws now" without
    // depending on call-count ordering across the whole shared test host.

    [Fact]
    public async Task Catalog_from_a_source_whose_Key_throws_after_routing_is_empty_not_500()
    {
        var client = _factory.CreateClient();
        // Forces the host (and SourceRouter, which reads every source's Key exactly once
        // while building the routing table) to finish starting up BEFORE the flag arms —
        // otherwise arming first can make Key throw during startup itself, which — after
        // the I2 fix — drops the source instead of reproducing "worked at startup, throws
        // now" for THIS test.
        await client.GetAsync("/health");

        Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", "1");
        try
        {
            var response = await client.GetAsync("/catalog/keyarmable:anything.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(json.GetProperty("items").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", null);
        }
    }

    [Fact]
    public async Task Detail_from_a_source_whose_Key_throws_after_routing_is_empty_not_500()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/health"); // see comment above: must outlive startup unarmed

        Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", "1");
        try
        {
            var response = await client.GetAsync("/detail/movie/keyarmable:anything.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(json.GetProperty("items").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", null);
        }
    }

    [Fact]
    public async Task Catalog_from_a_source_whose_SearchAsync_and_Key_both_throw_is_empty_not_500()
    {
        // Before the I1 fix, the catch block's own log call read source.Key directly —
        // if Key is the broken member, the handler throws from inside the catch and
        // 500s the request anyway, even though SearchAsync's failure was already handled.
        var client = _factory.CreateClient();
        await client.GetAsync("/health"); // see comment above: must outlive startup unarmed

        Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", "1");
        try
        {
            var response = await client.GetAsync("/catalog/keyarmablemethodsthrow:anything.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(json.GetProperty("items").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", null);
        }
    }

    // C1 (regression, over real HTTP): "canceled" throws OperationCanceledException from
    // every member without the request ever actually being cancelled — proves the fix at
    // the manifest AND at catalog/detail, not just at the unit level.

    [Fact]
    public async Task Manifest_still_lists_the_healthy_plugin_even_though_another_sources_Catalogs_getter_throws_OperationCanceledException()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        // Same "idx:*" caveat as Manifest_lists_the_plugins_catalog: filter to what the
        // plugins themselves contributed before asserting exclusivity.
        var catalog = Assert.Single(PluginCatalogs(json));
        Assert.Equal("good:all", catalog.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Catalog_from_a_source_whose_SearchAsync_throws_OperationCanceledException_is_empty_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/catalog/canceled:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Detail_from_a_source_whose_DetailAsync_throws_OperationCanceledException_is_empty_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/detail/movie/canceled:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }
}
