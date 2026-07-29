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
    public async Task With_no_indexer_configured_the_catalogs_are_empty_but_the_manifest_still_works()
    {
        // The project's stated property, asserted rather than claimed.
        using var factory = new StockServerFactory();   // writes a config with Indexers: []
        var client = factory.CreateClient();

        var manifest = await client.GetFromJsonAsync<JsonElement>("/manifest.json");
        Assert.NotEmpty(manifest.GetProperty("catalogs").EnumerateArray());

        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=anything.json");
        Assert.Empty(catalog.GetProperty("items").EnumerateArray());
    }
}
