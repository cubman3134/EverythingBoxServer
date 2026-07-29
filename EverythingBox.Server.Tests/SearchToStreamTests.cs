using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EverythingBox.Server.Tests;

/// <summary>Answers Torznab queries with a fixed two-release feed, and any other
/// request with 404 so an accidental live call is obvious rather than silent.</summary>
file sealed class FakeTorznabHandler : DelegatingHandler
{
    public int Queries { get; private set; }

    private const string Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Example Release One 1080p</title>
              <guid>https://example.test/1</guid>
              <enclosure url="https://example.test/one.torrent" type="application/x-bittorrent" length="1500000000" />
              <torznab:attr name="seeders" value="50" />
              <torznab:attr name="infohash" value="1111111111111111111111111111111111111111" />
            </item>
            <item>
              <title>Example Release Two 720p</title>
              <guid>https://example.test/2</guid>
              <enclosure url="https://example.test/two.torrent" type="application/x-bittorrent" length="800000000" />
              <torznab:attr name="seeders" value="5" />
              <torznab:attr name="infohash" value="2222222222222222222222222222222222222222" />
            </item>
          </channel>
        </rss>
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/api", StringComparison.OrdinalIgnoreCase))
        {
            Queries++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed, Encoding.UTF8, "application/xml"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

[Collection(AddonServerCollection.Name)]
public class SearchToStreamTests
{
    private readonly SearchServerFactory _factory;
    public SearchToStreamTests(SearchServerFactory factory) => _factory = factory;

    [Fact]
    public async Task The_manifest_lists_the_search_catalogs()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        var ids = json.GetProperty("catalogs").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ToArray();

        Assert.Contains("idx:movies", ids);
        Assert.Contains("idx:series", ids);
    }

    [Fact]
    public async Task The_manifest_declares_the_non_builtin_types_its_catalogs_use()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/manifest.json");

        var declared = json.GetProperty("mediaTypes").EnumerateArray()
            .Select(t => t.GetProperty("type").GetString())
            .ToArray();

        Assert.Contains("music", declared);
        Assert.Contains("book", declared);
        Assert.DoesNotContain("movie", declared);   // built into the client
    }

    [Fact]
    public async Task Searching_a_catalog_returns_releases_from_the_configured_indexer()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");

        var titles = json.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString())
            .ToArray();

        Assert.Contains("Example Release One 1080p", titles);
        Assert.Contains("Example Release Two 720p", titles);
    }

    [Fact]
    public async Task Every_returned_item_is_addressed_to_the_indexer_source()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");

        Assert.All(json.GetProperty("items").EnumerateArray(),
            i => Assert.StartsWith("idx:", i.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Resolving_a_returned_item_yields_a_playable_url()
    {
        var client = _factory.CreateClient();
        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");
        var id = catalog.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/movie/{id}.json");

        Assert.True(stream.TryGetProperty("url", out var url), "no url in the stream response");
        Assert.StartsWith("https://", url.GetString());
    }

    [Fact]
    public async Task Resolving_a_multi_file_release_never_hands_back_the_whole_torrent_zip_first()
    {
        // C1, exercised through the real HTTP surface: StubTorBoxHandler reports release
        // one with two files, which makes the real TorBoxService prepend a whole-torrent
        // zip at index 0 — exactly the shape that made a configured indexer's default
        // stream unplayable. ?n=0 (the client's default) must land on a real media file.
        var client = _factory.CreateClient();
        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");
        var id = catalog.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/movie/{id}.json");

        var url = stream.GetProperty("url").GetString();
        // Names the exact file expected, not just the extension: the fixture also
        // contains release-one-sample.mkv, which would satisfy an EndsWith(".mkv")
        // check just as well as the real feature file would.
        Assert.EndsWith("release-one.mkv", url);
    }

    [Fact]
    public async Task An_uncached_release_returns_the_caching_notice_rather_than_a_bare_no_source()
    {
        var client = _factory.CreateClient();
        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");

        // The stub debrid reports the SECOND release as still caching.
        var id = catalog.GetProperty("items").EnumerateArray().Last().GetProperty("id").GetString();

        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/movie/{id}.json");

        Assert.Empty(stream.GetProperty("streams").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(stream.GetProperty("notice").GetString()));
    }

    [Fact]
    public async Task Searching_the_series_catalog_asks_the_indexer_for_a_tv_search()
    {
        // Guards the vocabulary mapping end to end: the series shelf must not send a
        // movie query. Asserted through the real HTTP surface, not a unit stub.
        var client = _factory.CreateClient();
        await client.GetAsync("/catalog/idx:series/search=example.json");

        Assert.Contains("t=tvsearch", _factory.LastIndexerQuery);
    }
}

/// <summary>
/// Captures the query string of the most recent request bound for the fake indexer, so
/// <see cref="SearchToStreamTests.Searching_the_series_catalog_asks_the_indexer_for_a_tv_search"/>
/// can assert on the wire-level Torznab function without a unit-level stub. Sits in front of
/// <see cref="FakeTorznabHandler"/> in the handler chain rather than replacing it — the feed
/// content itself is still that class's responsibility, verbatim.
/// <para>
/// A file-local handler type cannot be exposed as a field's type on a non-file-local class
/// (CS9051) — <see cref="SearchServerFactory"/> is public, so it can't hold a
/// <see cref="QueryCapturingHandler"/> reference directly. Reporting back through a plain
/// <see cref="string"/>-typed callback sidesteps that without widening this type's visibility.
/// </para>
/// </summary>
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
/// Stands in for a real TorBox account, so <see cref="SearchServerFactory"/> can exercise the
/// real <c>TorBoxService</c> (Core's actual <see cref="IDebridService"/> implementation, chosen
/// via config the same way a real deployment would) rather than a hand-rolled test double —
/// <see cref="IDebridService"/> is never itself a DI-resolvable service (<c>Program.cs</c> builds
/// it inline from config inside the <c>SourceRouter</c> factory), so the only seam available to a
/// test is the shared <see cref="HttpClient"/> TorBoxService calls through.
/// <para>
/// Answers exactly the three calls <c>TorBoxService.ResolveAsync</c> makes for a cached-only
/// (<c>MaxWait = TimeSpan.Zero</c>) resolution: <c>createtorrent</c> (dedupes by info hash — the
/// magnet embeds it, so the hash in the request body says which of the two fixture releases this
/// is), <c>mylist</c> (reports release one as already finished — an instant "cached" result, with
/// MORE THAN ONE file so TorBoxService.RequestLinksAsync also fetches a whole-torrent zip link and
/// inserts it at index 0 — the exact real-world shape C1 fixed — and release two as still
/// downloading, which is Pending immediately since MaxWait is zero), and <c>requestdl</c> (one
/// link per file plus the zip link, for release one only — TorBoxService never asks for a link
/// for a release <c>mylist</c> reported as not ready).
/// </para>
/// </summary>
file sealed class StubTorBoxHandler : DelegatingHandler
{
    private const string HashOne = "1111111111111111111111111111111111111111";
    private const string HashTwo = "2222222222222222222222222222222222222222";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (!string.Equals(uri.Host, "api.torbox.app", StringComparison.OrdinalIgnoreCase))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var path = uri.AbsolutePath;

        if (path.EndsWith("createtorrent", StringComparison.OrdinalIgnoreCase))
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var id = body.Contains(HashOne, StringComparison.OrdinalIgnoreCase) ? 1
                : body.Contains(HashTwo, StringComparison.OrdinalIgnoreCase) ? 2
                : 0;

            return id == 0
                ? Json(new { success = false, detail = "unrecognized magnet" })
                : Json(new { success = true, data = new { torrent_id = id } });
        }

        if (path.EndsWith("mylist", StringComparison.OrdinalIgnoreCase))
        {
            var id = QueryValue(uri, "id");
            var ready = id == "1";
            return Json(new
            {
                success = true,
                data = new
                {
                    download_finished = ready,
                    download_present = ready,
                    // Two files (not one) so TorBoxService.RequestLinksAsync also requests
                    // a whole-torrent zip link and inserts it at index 0 — the real shape
                    // that made C1 true for every multi-file release before it was fixed.
                    files = ready
                        ? new object[]
                        {
                            new { id = 11, name = "release-one.mkv", size = 1_500_000_000L },
                            new { id = 12, name = "release-one-sample.mkv", size = 50_000_000L },
                        }
                        : [],
                },
            });
        }

        if (path.EndsWith("requestdl", StringComparison.OrdinalIgnoreCase))
        {
            if (QueryValue(uri, "zip_link") == "true")
                return Json(new { success = true, data = "https://example.test/download/release-one.zip" });

            var url = QueryValue(uri, "file_id") switch
            {
                "11" => "https://example.test/download/release-one.mkv",
                "12" => "https://example.test/download/release-one-sample.mkv",
                _ => "https://example.test/download/unknown",
            };
            return Json(new { success = true, data = url });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string? QueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && Uri.UnescapeDataString(part[..equals]) == key)
                return Uri.UnescapeDataString(part[(equals + 1)..]);
        }
        return null;
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// Boots the real host (reusing the <see cref="PluginServerFactory"/> pattern of writing a
/// config file and process-wide <c>EBS_*</c> environment variables) with one Torznab indexer and
/// a TorBox debrid, both served entirely by fake handlers installed in place of the
/// <see cref="HttpClient"/> singleton <c>Program.cs</c> registers — no live network call is ever
/// made. This is what lets <see cref="SearchToStreamTests"/> prove search-to-stream through the
/// real HTTP surface rather than a unit-level stub.
/// <para>
/// Joins <see cref="AddonServerCollection"/> rather than defining its own collection: it writes
/// the same process-wide <c>EBS_*</c> environment variables <see cref="PluginServerFactory"/>
/// does, and that collection (not a fresh one) is what serializes this fixture's writes against
/// every other one that touches them. See that collection's doc comment.
/// </para>
/// </summary>
public sealed class SearchServerFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-search-host-" + Guid.NewGuid().ToString("N"));
    private string? _lastIndexerQuery;

    public string FilesDirectory => Path.Combine(_root, "files");

    /// <summary>The query string of the most recent request the fake indexer received.</summary>
    public string? LastIndexerQuery => _lastIndexerQuery;

    public SearchServerFactory()
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

        // xUnit constructs every ICollectionFixture registered on AddonServerCollection before
        // any test in it runs — without forcing the host to build right here, whichever fixture
        // is constructed after this one (currently PluginServerFactory, see its own doc comment)
        // would overwrite these env vars before Program.cs's top-level statements, which only
        // ever run once per host, get a chance to read them.
        CreateClient().Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Replaces the HttpMessageHandler singleton Program.cs registers — not the
            // HttpClient built on top of it — because GrabberFactory.WrapWithRetry chains
            // RetryHandler onto THIS handler for every indexer/debrid call (see that method's
            // doc comment). Overriding HttpClient alone would leave the pipeline's own transport
            // untouched and every request in this test would go out over a real HttpClientHandler.
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
