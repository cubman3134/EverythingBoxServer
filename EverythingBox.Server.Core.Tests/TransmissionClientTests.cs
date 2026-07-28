using System.Net;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Download.Transmission;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class TransmissionClientTests
{
    private static TorrentResult Magnet(string hash = "ABC123")
        => new()
        {
            Title = "The Matrix 1999 1080p BluRay",
            ProviderName = "test",
            MagnetUri = new Uri($"magnet:?xt=urn:btih:{hash}"),
            InfoHash = hash,
        };

    private static TransmissionOptions Options => new()
    {
        BaseUrl = new Uri("http://localhost:9091"),
        Username = "admin",
        Password = "secret",
    };

    private const string SuccessBody =
        """{"result":"success","arguments":{"torrent-added":{"hashString":"abc123","id":7,"name":"The Matrix"}}}""";

    [Fact]
    public async Task PerformsSessionHandshakeThenAdds()
    {
        var calls = 0;
        var handler = new StubHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                // Transmission's CSRF challenge.
                var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                conflict.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", "sess-xyz");
                return conflict;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SuccessBody) };
        });

        var client = new TransmissionClient(new HttpClient(handler), Options);
        var result = await client.AddAsync(Magnet(), new DownloadOptions { SavePath = "/movies" });

        Assert.True(result.Success);
        Assert.Equal("abc123", result.InfoHash);

        Assert.Equal(2, handler.Requests.Count);
        // The retried request echoes the session id back.
        Assert.Contains("sess-xyz", handler.Requests[1].Headers.GetValues("X-Transmission-Session-Id"));
        // Basic auth was attached.
        Assert.Equal("Basic", handler.Requests[1].Headers.Authorization!.Scheme);
        // Body is a torrent-add carrying the magnet and download dir.
        Assert.Contains("torrent-add", handler.Bodies[1]);
        Assert.Contains("urn:btih:ABC123", handler.Bodies[1]);
        Assert.Contains("/movies", handler.Bodies[1]);
    }

    [Fact]
    public async Task ReturnsFailedOnRpcError()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"result":"invalid or corrupt torrent file"}"""),
            });

        var client = new TransmissionClient(new HttpClient(handler), Options);
        var result = await client.AddAsync(Magnet());

        Assert.False(result.Success);
        Assert.Contains("corrupt", result.Message);
    }

    [Fact]
    public async Task ReturnsFailedWhenNoLink()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new TransmissionClient(new HttpClient(handler), Options);

        var result = await client.AddAsync(Magnet() with { MagnetUri = null, DownloadUrl = null });

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(request);
            Bodies.Add(body);
            return responder(request, body);
        }
    }
}
