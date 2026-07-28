using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EverythingBox.Server.Tests;

/// <summary>Drives the real host over HTTP with the "good" fixture plugin installed.</summary>
[Collection(AddonServerCollection.Name)]
public class AddonStreamTests
{
    private readonly PluginServerFactory _factory;
    public AddonStreamTests(PluginServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Resolves_a_playable_stream()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/stream/movie/good:alpha.json");

        Assert.Equal("https://example.test/alpha.mkv", json.GetProperty("url").GetString());
        Assert.Equal("video/x-matroska", json.GetProperty("mime").GetString());
        Assert.False(json.TryGetProperty("curl", out _));
    }

    [Fact]
    public async Task Passes_the_index_through_so_a_user_can_reject_a_result()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/stream/movie/good:indexed.json?n=2");

        Assert.Equal("https://example.test/pick-2.mkv", json.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Marks_a_stream_the_client_should_fetch_itself()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/stream/game/good:curl.json?dl=curl");

        Assert.True(json.GetProperty("curl").GetBoolean());
    }

    [Fact]
    public async Task Returns_a_notice_when_nothing_is_playable_yet()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/stream/movie/good:notice.json");

        Assert.Empty(json.GetProperty("streams").EnumerateArray());
        Assert.Equal("caching, retry shortly", json.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task Refuses_a_url_the_client_could_not_play()
    {
        var json = await _factory.CreateClient()
            .GetFromJsonAsync<JsonElement>("/stream/movie/good:unsafe.json");

        Assert.Empty(json.GetProperty("streams").EnumerateArray());
        Assert.False(json.TryGetProperty("url", out _));
    }

    [Theory]
    [InlineData("/stream/movie/good:missing.json")]
    [InlineData("/stream/movie/nosuch:thing.json")]
    public async Task Returns_empty_streams_when_there_is_nothing(string path)
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>(path);
        Assert.Empty(json.GetProperty("streams").EnumerateArray());
    }

    [Fact]
    public async Task Proxies_a_body_on_the_sources_behalf()
    {
        var response = await _factory.CreateClient().GetAsync("/proxy/good/proxied/file.bin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PROXIED-BODY", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Forwards_the_range_header_to_the_source()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/proxy/good/proxied/file.bin");
        request.Headers.Add("Range", "bytes=0-3");

        var response = await _factory.CreateClient().SendAsync(request);

        // The fixture only sets Content-Range when a range header reached it.
        Assert.Equal("bytes 0-3/12", response.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task Proxy_is_404_when_the_source_does_not_serve_that_item()
    {
        var response = await _factory.CreateClient().GetAsync("/proxy/good/not-proxied/file.bin");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // F1: the "throwing" fixture plugin is installed alongside "good" specifically so these
    // tests can prove request-time containment over real HTTP.

    [Fact]
    public async Task Stream_from_a_source_whose_ResolveAsync_throws_is_empty_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/stream/movie/throwing:anything.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(json.GetProperty("streams").EnumerateArray());
    }

    [Fact]
    public async Task Proxy_from_a_source_whose_OpenAsync_throws_is_404_not_500()
    {
        var response = await _factory.CreateClient().GetAsync("/proxy/throwing/anything/file.bin");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // C4 (regression): the proxy route stopped disposing the ProxyResponse a source hands
    // it. LocalFolderSource.OpenAsync returns a File.OpenRead stream, so this leaked a file
    // handle and a Windows file lock on every /proxy/... request. GoodSource's
    // "disposal-tracked" item returns a stream that records its own disposal via a
    // process-wide env var (the only channel that survives crossing this fixture's
    // AssemblyLoadContext boundary back to the test) so this can be proven over a REAL
    // request, not just the unit-level DisposeAsync try/finally test.
    [Fact]
    public async Task Proxy_disposes_the_sources_ProxyResponse_after_the_response_completes()
    {
        Environment.SetEnvironmentVariable("EBS_TEST_PROXY_BODY_DISPOSED", null);
        try
        {
            var response = await _factory.CreateClient().GetAsync("/proxy/good/disposal-tracked/file.bin");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("TRACKED-BODY", await response.Content.ReadAsStringAsync());

            Assert.Equal("1", Environment.GetEnvironmentVariable("EBS_TEST_PROXY_BODY_DISPOSED"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_TEST_PROXY_BODY_DISPOSED", null);
        }
    }

    // I1: the proxy catch block's own log call reads source.Key too — if OpenAsync throws
    // AND Key is the broken member, naive logging throws again from inside the catch and
    // 500s the request even though OpenAsync's failure was already meant to be handled.
    [Fact]
    public async Task Proxy_from_a_source_whose_OpenAsync_and_Key_both_throw_is_404_not_500()
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
            var response = await client.GetAsync("/proxy/keyarmablemethodsthrow/anything/file.bin");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", null);
        }
    }

    // I1: same for the stream catch block.
    [Fact]
    public async Task Stream_from_a_source_whose_ResolveAsync_and_Key_both_throw_is_empty_not_500()
    {
        var client = _factory.CreateClient();
        await client.GetAsync("/health"); // see comment above: must outlive startup unarmed

        Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", "1");
        try
        {
            var response = await client.GetAsync("/stream/movie/keyarmablemethodsthrow:anything.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(json.GetProperty("streams").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_TEST_KEY_ARMED", null);
        }
    }
}
