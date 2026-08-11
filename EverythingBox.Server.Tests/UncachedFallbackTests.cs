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

[Collection(AddonServerCollection.Name)]
public class UncachedFallbackTests
{
    private readonly FallbackServerFactory _factory;
    public UncachedFallbackTests(FallbackServerFactory factory) => _factory = factory;

    private async Task<string> FirstItemIdAsync(HttpClient client)
    {
        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");
        return catalog.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task An_uncached_release_is_served_as_a_hosted_file()
    {
        var client = _factory.CreateClient();
        var id = await FirstItemIdAsync(client);

        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/movie/{id}.json");

        var url = stream.GetProperty("url").GetString()!;
        Assert.StartsWith("files/", url);
    }

    [Fact]
    public async Task The_hosted_file_actually_serves_bytes()
    {
        // A url that 404s would satisfy the assertion above and be useless.
        var client = _factory.CreateClient();
        var id = await FirstItemIdAsync(client);
        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/movie/{id}.json");
        var url = stream.GetProperty("url").GetString()!;

        var file = await client.GetAsync("/" + url);

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.NotEmpty(await file.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_downloader_is_asked_once_even_for_two_simultaneous_viewers()
    {
        var client = _factory.CreateClient();
        var id = await FirstItemIdAsync(client);

        var first = client.GetAsync($"/stream/movie/{id}.json");
        var second = client.GetAsync($"/stream/movie/{id}.json");
        await Task.WhenAll(first, second);

        Assert.Equal(1, _factory.DownloadCalls);
    }
}

public class UncachedWithoutFallbackTests
{
    [Fact]
    public async Task With_downloading_off_the_user_gets_the_caching_notice()
    {
        // The shipped default, asserted rather than assumed.
        using var factory = new NoFallbackServerFactory();
        var client = factory.CreateClient();

        var catalog = await client.GetFromJsonAsync<JsonElement>("/catalog/idx:movies/search=example.json");
        var id = catalog.GetProperty("items").EnumerateArray().First().GetProperty("id").GetString();

        var stream = await client.GetFromJsonAsync<JsonElement>($"/stream/movie/{id}.json");

        Assert.Empty(stream.GetProperty("streams").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(stream.GetProperty("notice").GetString()));
        Assert.Equal(0, factory.DownloadCalls);
    }
}

/// <summary>Produces one file per call and counts how often it was asked.</summary>
public sealed class CountingDownloader : ITorrentDownloader
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent, MediaRequest? request, string directory,
        IProgress<TorrentDownloadProgress>? progress = null, long? maxTotalBytes = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Example.Release.1080p.mkv");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return Task.FromResult<IReadOnlyList<string>>([path]);
    }
}

public abstract class FallbackFactoryBase(bool enabled, string prefix) : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
    private readonly CountingDownloader _downloader = new();

    public int DownloadCalls => _downloader.Calls;

    protected void Prepare()
    {
        var files = Path.Combine(_root, "files");
        Directory.CreateDirectory(files);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, $$"""
            {
              "Indexers": [
                { "Name": "Example Indexer", "BaseUrl": "http://indexer.example.test/api" }
              ],
              "Debrid": { "Provider": "torbox", "ApiKey": "test-key" },
              "Download": { "Enabled": {{(enabled ? "true" : "false")}}, "MaxSizeMB": 4096, "TimeoutSeconds": 30 }
            }
            """);

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", Path.Combine(_root, "plugins"));
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", files);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);

        // Same reason SearchServerFactory does this: whichever collection fixture is
        // constructed next would otherwise overwrite these env vars before Program.cs's
        // top-level statements read them.
        CreateClient().Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // The debrid stub must report Pending for every release — that is the only
            // outcome the fallback hooks into.
            services.AddSingleton<HttpMessageHandler>(_ =>
                new PendingTorBoxHandler { InnerHandler = new FakeTorznabHandler { InnerHandler = new HttpClientHandler() } });

            services.AddSingleton<ITorrentDownloader>(_ => _downloader);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

public sealed class FallbackServerFactory : FallbackFactoryBase
{
    public FallbackServerFactory() : base(enabled: true, "ebs-fallback-host-") => Prepare();
}

public sealed class NoFallbackServerFactory : FallbackFactoryBase
{
    public NoFallbackServerFactory() : base(enabled: false, "ebs-nofallback-host-") => Prepare();
}

/// <summary>
/// Stands in for a TorBox account on which every release is still caching. Answers the two
/// calls <c>TorBoxService.ResolveAsync</c> makes before it can conclude "not cached":
/// <c>createtorrent</c> (accepts the magnet and hands back a torrent id) and <c>mylist</c>
/// (reports the torrent as neither <c>download_finished</c> nor <c>download_present</c>, with
/// no files). With <see cref="Core.Debrid.TorBox.TorBoxOptions.MaxWait"/> defaulting to
/// <see cref="TimeSpan.Zero"/>, that first not-ready poll trips <c>stopwatch.Elapsed &gt;=
/// MaxWait</c> and returns <see cref="DebridStatus.Pending"/> — the one outcome the
/// self-download fallback hooks into. Never answers <c>requestdl</c>: a Pending resolution
/// never asks for a link. Modelled on <c>StubTorBoxHandler</c> in <c>SearchToStreamTests.cs</c>.
/// </summary>
file sealed class PendingTorBoxHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (!string.Equals(uri.Host, "api.torbox.app", StringComparison.OrdinalIgnoreCase))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var path = uri.AbsolutePath;

        if (path.EndsWith("createtorrent", StringComparison.OrdinalIgnoreCase))
            return Json(new { success = true, data = new { torrent_id = 1 } });

        if (path.EndsWith("mylist", StringComparison.OrdinalIgnoreCase))
            return Json(new
            {
                success = true,
                data = new { download_finished = false, download_present = false, files = Array.Empty<object>() },
            });

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}

/// <summary>
/// Answers Torznab queries with a fixed two-release feed, and any other request with 404 so an
/// accidental live call is obvious rather than silent. A file-local copy of the identically
/// named handler in <c>SearchToStreamTests.cs</c> (that one is <c>file</c>-scoped, so it isn't
/// visible here); the feed is the same so the fallback path exercises the same releases.
/// </summary>
file sealed class FakeTorznabHandler : DelegatingHandler
{
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Feed, Encoding.UTF8, "application/xml"),
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
