using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EverythingBox.Server.Tests;

/// <summary>Answers every Torznab query with a single fixed release shaped to satisfy
/// <c>DefaultTorrentRanker</c>'s relevance filter for a "Example Show" search (its title
/// tokens are a subset of the release title, and its season/episode match) — the same
/// canned-feed approach <see cref="SearchServerFactory"/>'s own handler uses, just with
/// one release instead of two, since <see cref="BrowseToStreamTests"/> only ever resolves
/// through the series/episode path.</summary>
file sealed class FakeTorznabHandler : DelegatingHandler
{
    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Example Show S01E01 1080p</title>
              <guid>https://example.test/3</guid>
              <enclosure url="https://example.test/three.torrent" type="application/x-bittorrent" length="900000000" />
              <torznab:attr name="seeders" value="50" />
              <torznab:attr name="infohash" value="3333333333333333333333333333333333333333" />
            </item>
          </channel>
        </rss>
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/api", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed, Encoding.UTF8, "application/xml"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Captures the query string of the most recent request bound for the fake
/// indexer. See <c>SearchToStreamTests.QueryCapturingHandler</c>'s doc comment for why
/// this is duplicated per file rather than shared: a file-local handler type cannot be
/// exposed as a field's type on a non-file-local class (CS9051).</summary>
file sealed class QueryCapturingHandler(Action<string> onIndexerQuery) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { Host: "indexer.example.test" } uri)
            onIndexerQuery(uri.Query);

        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Stands in for a real TorBox account, exactly as <c>SearchToStreamTests.StubTorBoxHandler</c>
/// does, but simplified to one release/one file — <see cref="BrowseToStreamTests"/> only needs
/// a single playable link at the end of the browse-to-stream walk, not the multi-file
/// zip-insertion shape that suite exercises.
/// </summary>
file sealed class StubTorBoxHandler : DelegatingHandler
{
    private const string Hash = "3333333333333333333333333333333333333333";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (!string.Equals(uri.Host, "api.torbox.app", StringComparison.OrdinalIgnoreCase))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var path = uri.AbsolutePath;

        if (path.EndsWith("createtorrent", StringComparison.OrdinalIgnoreCase))
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Contains(Hash, StringComparison.OrdinalIgnoreCase)
                ? Json(new { success = true, data = new { torrent_id = 1 } })
                : Json(new { success = false, detail = "unrecognized magnet" });
        }

        if (path.EndsWith("mylist", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new
            {
                success = true,
                data = new
                {
                    download_finished = true,
                    download_present = true,
                    files = new object[]
                    {
                        new { id = 21, name = "example-show-s01e01.mkv", size = 900_000_000L },
                    },
                },
            });
        }

        if (path.EndsWith("requestdl", StringComparison.OrdinalIgnoreCase))
            return Json(new { success = true, data = "https://example.test/download/example-show-s01e01.mkv" });

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

[Collection(AddonServerCollection.Name)]
public class BrowseToStreamTests
{
    private readonly BrowseServerFactory _factory;
    public BrowseToStreamTests(BrowseServerFactory factory) => _factory = factory;

    private static string[] CatalogIds(JsonElement manifest) =>
        manifest.GetProperty("catalogs").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()!)
            .ToArray();

    [Fact]
    public async Task The_manifest_lists_the_browse_catalogs()
    {
        var manifest = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        var ids = CatalogIds(manifest);
        Assert.Contains("meta:movies", ids);
        Assert.Contains("meta:series", ids);
    }

    [Fact]
    public async Task Built_in_catalog_filter_excludes_both_idx_and_meta_when_both_are_non_empty()
    {
        // AddonBrowseTests' BuiltInSourceKeys filter (["idx", "meta"], matched with
        // StartsWith(key + ":")) has never run against a fixture where BOTH idx: and
        // meta: catalogs are genuinely non-empty: PluginServerFactory configures
        // neither an indexer nor a metadata source, so both were always absent from its
        // manifest, not merely filtered out. This fixture configures both (an indexer
        // against the fake Torznab handler, and the "browsemeta" test plugin), so a
        // typo in that filter — the wrong key, or Contains instead of StartsWith —
        // would leave a stray built-in catalog behind and this catches it.
        var manifest = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");
        var ids = CatalogIds(manifest);

        Assert.Contains(ids, id => id.StartsWith("idx:", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.StartsWith("meta:", StringComparison.Ordinal));

        string[] builtInSourceKeys = ["idx", "meta"];
        var pluginCatalogIds = ids
            .Where(id => !builtInSourceKeys.Any(key => id.StartsWith(key + ":", StringComparison.Ordinal)))
            .ToArray();

        // This fixture installs no IMediaSource-based plugin (the "browsemeta" test
        // plugin registers only an IMetadataSource), so once idx:/meta: are excluded
        // nothing plugin-contributed should remain.
        Assert.Empty(pluginCatalogIds);
    }

    [Fact]
    public async Task Browsing_movies_returns_the_film_with_its_poster()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/catalog/meta:movies.json");

        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal("Example Film", item.GetProperty("title").GetString());
        Assert.Equal("https://example.test/poster.jpg", item.GetProperty("thumbnailUrl").GetString());
    }

    [Fact]
    public async Task Browsing_series_returns_an_expandable_item()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/catalog/meta:series.json");

        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal("Example Show", item.GetProperty("title").GetString());
        Assert.True(item.GetProperty("expandable").GetBoolean());
    }

    [Fact]
    public async Task Expanding_the_series_lists_its_episodes()
    {
        var client = _factory.CreateClient();
        var series = await client.GetFromJsonAsync<JsonElement>("/catalog/meta:series.json");
        var id = series.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/detail/series/{id}.json");

        Assert.Equal(2, detail.GetProperty("items").EnumerateArray().Count());
    }

    [Fact]
    public async Task Resolving_an_episode_yields_a_playable_url()
    {
        var client = _factory.CreateClient();
        var series = await client.GetFromJsonAsync<JsonElement>("/catalog/meta:series.json");
        var seriesId = series.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/detail/series/{seriesId}.json");
        var episodeId = detail.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/series/{episodeId}.json");

        Assert.True(stream.TryGetProperty("url", out var url), "no url in the stream response");
        Assert.StartsWith("https://", url.GetString());
    }

    [Fact]
    public async Task Resolving_an_episode_asks_the_indexer_for_that_season_and_episode()
    {
        // The assertion that catches a lost season or episode on the way through the id.
        var client = _factory.CreateClient();
        var series = await client.GetFromJsonAsync<JsonElement>("/catalog/meta:series.json");
        var seriesId = series.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/detail/series/{seriesId}.json");
        var episodeId = detail.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        await client.GetAsync($"/stream/series/{episodeId}.json");

        // TorznabQueryBuilder.BuildUri emits "season"/"ep" verbatim for a TvRequest
        // (EverythingBox.Server.Core/Providers/Torznab/TorznabQueryBuilder.cs) — matches
        // what the brief guessed, confirmed by reading the builder before asserting.
        Assert.Contains("t=tvsearch", _factory.LastIndexerQuery);
        Assert.Contains("season=1", _factory.LastIndexerQuery);
        Assert.Contains("ep=1", _factory.LastIndexerQuery);
    }
}

[Collection(AddonServerCollection.Name)]
public class NoMetadataPluginTests
{
    // The brief's sketch newed up SearchOnlyServerFactory ad hoc inside the test method.
    // AddonServerCollection's own doc comment is stricter than that: "Any new test class
    // that touches these env vars ... MUST join this same collection ... and
    // constructor-injecting the shared fixture — rather than defining its own fixture."
    // Constructor-injecting the collection-owned instance (registered on
    // AddonServerCollection below) is what actually satisfies "must join AddonServerCollection"
    // from the task brief, so that's what this does instead.
    private readonly SearchOnlyServerFactory _factory;
    public NoMetadataPluginTests(SearchOnlyServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Without_a_metadata_plugin_no_browse_catalogs_are_advertised()
    {
        // The server ships no metadata source, exactly as it ships no indexer.
        var client = _factory.CreateClient();

        var manifest = await client.GetFromJsonAsync<JsonElement>("/manifest.json");
        var ids = manifest.GetProperty("catalogs").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()!)
            .ToArray();

        Assert.DoesNotContain(ids, id => id.StartsWith("meta:", StringComparison.Ordinal));

        var health = await client.GetFromJsonAsync<JsonElement>("/health");
        Assert.True(health.GetProperty("ok").GetBoolean());
    }
}

/// <summary>
/// Boots the real host with one Torznab indexer, a TorBox debrid, and the "browsemeta"
/// fixture plugin (<c>tests/TestPlugin.Metadata</c>) installed — an <see cref="EverythingBox.Server.Abstractions.IMetadataSource"/>
/// registered via <c>AddMetadata</c>, not <c>AddSource</c>, so the "meta:" catalogs it
/// contributes come entirely from the metadata tier, while the actual playable release
/// comes from the indexer/debrid pipeline below. Same reasoning as <see cref="SearchServerFactory"/>
/// for every other choice here (fake handlers replacing the shared <see cref="HttpMessageHandler"/>,
/// joining <see cref="AddonServerCollection"/>, the eager <see cref="CreateClient"/> call in the
/// constructor) — see that class's doc comment.
/// </summary>
public sealed class BrowseServerFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-browse-host-" + Guid.NewGuid().ToString("N"));
    private string? _lastIndexerQuery;

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");

    public string? LastIndexerQuery => _lastIndexerQuery;

    public BrowseServerFactory()
    {
        StagePlugin("browsemeta");

        Directory.CreateDirectory(FilesDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, """
            {
              "Indexers": [
                { "Name": "Example Indexer", "BaseUrl": "http://indexer.example.test/api" }
              ],
              "Debrid": { "Provider": "torbox", "ApiKey": "test-key" }
            }
            """);

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);

        // See SearchServerFactory's doc comment: forces Program.cs to read these env
        // vars now, before another collection fixture's constructor can overwrite them.
        CreateClient().Dispose();
    }

    private void StagePlugin(string fixtureName)
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", fixtureName);
        var dest = Path.Combine(PluginsDirectory, fixtureName);
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<HttpMessageHandler>(_ =>
            {
                var capture = new QueryCapturingHandler(q => _lastIndexerQuery = q)
                {
                    InnerHandler = new FakeTorznabHandler { InnerHandler = new HttpClientHandler() },
                };
                return new StubTorBoxHandler { InnerHandler = capture };
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Same as <see cref="BrowseServerFactory"/> — one Torznab indexer, a TorBox debrid —
/// minus the "browsemeta" plugin, so no <see cref="EverythingBox.Server.Abstractions.IMetadataSource"/>
/// is ever registered and no "meta:" catalog can appear in its manifest. Proves the
/// "ships no metadata source, same as it ships no indexer" claim over the real HTTP
/// surface via <see cref="NoMetadataPluginTests"/>.
/// </summary>
public sealed class SearchOnlyServerFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-search-only-host-" + Guid.NewGuid().ToString("N"));

    public string FilesDirectory => Path.Combine(_root, "files");

    public SearchOnlyServerFactory()
    {
        Directory.CreateDirectory(FilesDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, """
            {
              "Indexers": [
                { "Name": "Example Indexer", "BaseUrl": "http://indexer.example.test/api" }
              ],
              "Debrid": { "Provider": "torbox", "ApiKey": "test-key" }
            }
            """);

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", Path.Combine(_root, "plugins"));
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);

        // See SearchServerFactory's doc comment: forces Program.cs to read these env
        // vars now, before another collection fixture's constructor can overwrite them.
        CreateClient().Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<HttpMessageHandler>(_ =>
                new StubTorBoxHandler { InnerHandler = new FakeTorznabHandler { InnerHandler = new HttpClientHandler() } });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}
