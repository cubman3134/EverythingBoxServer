using System.Net.Http.Json;
using System.Text.Json;

namespace EverythingBox.Server.Tests;

// Joins AddonServerCollection (see StockServerFactory's doc comment) so this class's own
// StockServerFactory construction — which writes the same process-wide EBS_* environment
// variables every other fixture in that collection does — never races PluginServerFactory's
// or SearchServerFactory's. It does not take any collection fixture via constructor injection;
// a stock (no-indexer) config needs its own host instance, distinct from the shared ones.
[Collection(AddonServerCollection.Name)]
public class StockServerTests
{
    [Fact]
    public async Task With_no_indexer_configured_no_catalogs_are_advertised_but_the_manifest_still_works()
    {
        // The project's stated property, asserted rather than claimed: a stock install
        // with Indexers: [] must not advertise a shelf nothing can fill, so the manifest
        // declares none of IndexerSearchSource's catalogs at all.
        using var factory = new StockServerFactory();   // writes a config with Indexers: []
        var client = factory.CreateClient();

        var manifest = await client.GetFromJsonAsync<JsonElement>("/manifest.json");
        Assert.Empty(manifest.GetProperty("catalogs").EnumerateArray());

        // M2: IndexerSearchSource gated Catalogs on indexerCount > 0 but left
        // MediaTypes unconditional, so a stock install used to advertise presentation
        // hints (music/audiobook/book/comic) for four media types with no catalog to
        // appear on. MediaTypes must follow the same discipline as Catalogs.
        Assert.Empty(manifest.GetProperty("mediaTypes").EnumerateArray());

        // Even though it isn't advertised, a request for it is still handled gracefully —
        // an unknown/undeclared catalog id degrades to an empty catalog, not an error.
        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=anything.json");
        Assert.Empty(catalog.GetProperty("items").EnumerateArray());
    }
}
