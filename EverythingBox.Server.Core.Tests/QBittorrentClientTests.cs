using System.Net;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Download.QBittorrent;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class QBittorrentClientTests
{
    private static TorrentResult Magnet(string hash = "ABC123")
        => new()
        {
            Title = "The Matrix 1999 1080p BluRay",
            ProviderName = "test",
            MagnetUri = new Uri($"magnet:?xt=urn:btih:{hash}"),
            InfoHash = hash,
        };

    private static QBittorrentOptions Options(string? user = "admin") => new()
    {
        BaseUrl = new Uri("http://localhost:8080"),
        Username = user,
        Password = "secret",
    };

    [Fact]
    public async Task LogsInThenAddsMagnet()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/login"))
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                ok.Headers.TryAddWithoutValidation("Set-Cookie", "SID=session123; path=/");
                return ok;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
        });

        var client = new QBittorrentClient(new HttpClient(handler), Options());
        var result = await client.AddAsync(Magnet(), new DownloadOptions { Category = "movies" });

        Assert.True(result.Success);
        Assert.Equal("abc123", result.InfoHash);

        // Two calls: login then add.
        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/auth/login", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/torrents/add", handler.Requests[1].RequestUri!.AbsolutePath);

        // The add request carried the session cookie and the magnet + category.
        Assert.Contains("SID=session123", handler.Requests[1].Headers.GetValues("Cookie"));
        Assert.Contains("magnet:?xt=urn:btih:ABC123", handler.Bodies[1]); // literal in the multipart part
        Assert.Contains("name=category", handler.Bodies[1]);
        Assert.Contains("movies", handler.Bodies[1]);
    }

    [Fact]
    public async Task SkipsLoginWhenNoUsername()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") });

        var client = new QBittorrentClient(new HttpClient(handler), Options(user: null));
        var result = await client.AddAsync(Magnet());

        Assert.True(result.Success);
        Assert.Single(handler.Requests);
        Assert.EndsWith("/torrents/add", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ReturnsFailedWhenNoLink()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new QBittorrentClient(new HttpClient(handler), Options());

        var noLink = Magnet() with { MagnetUri = null, DownloadUrl = null };
        var result = await client.AddAsync(noLink);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests); // never hit the network
        Assert.Contains("no magnet", result.Message);
    }

    [Fact]
    public async Task ReturnsFailedWhenLoginRejected()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Fails.") });

        var client = new QBittorrentClient(new HttpClient(handler), Options());
        var result = await client.AddAsync(Magnet());

        Assert.False(result.Success);
        Assert.Equal("authentication failed", result.Message);
        Assert.Single(handler.Requests); // login only; add never attempted
    }

    [Fact]
    public async Task PrefersTorrentFileUrlWhenNoMagnet()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") });

        var client = new QBittorrentClient(new HttpClient(handler), Options(user: null));
        var torrentFile = Magnet() with
        {
            MagnetUri = null,
            DownloadUrl = new Uri("https://tracker.example/x.torrent"),
        };

        var result = await client.AddAsync(torrentFile);

        Assert.True(result.Success);
        Assert.Contains("x.torrent", handler.Bodies[0]);
    }

    [Fact]
    public async Task ReAuthenticatesOnceWhenTheSessionHasExpired()
    {
        // First login succeeds, the add 403s (expired session), the client logs in again
        // and the retried add succeeds.
        var logins = 0;
        var adds = 0;
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                logins++;
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                ok.Headers.TryAddWithoutValidation("Set-Cookie", $"SID=session{logins}; path=/");
                return ok;
            }

            adds++;
            return adds == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
        });

        var client = new QBittorrentClient(new HttpClient(handler), Options());
        var result = await client.AddAsync(Magnet(), new DownloadOptions());

        Assert.True(result.Success);
        Assert.Equal(2, logins);
        Assert.Equal(2, adds);
    }

    [Fact]
    public async Task GivesUpAfterOneReAuthentication()
    {
        // A genuinely wrong password must fail fast, not loop.
        var logins = 0;
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                logins++;
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                ok.Headers.TryAddWithoutValidation("Set-Cookie", $"SID=session{logins}; path=/");
                return ok;
            }
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });

        var client = new QBittorrentClient(new HttpClient(handler), Options());
        var result = await client.AddAsync(Magnet(), new DownloadOptions());

        Assert.False(result.Success);
        Assert.Equal(2, logins);
        Assert.Contains("session expired", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendsBothPausedAndStoppedSoItWorksAcrossMajorVersions()
    {
        // qB renamed the field; sending both is what makes one client work with 4.x and 5.x.
        // Nothing covered this before, so the compatibility branch could be dropped silently.
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
                ok.Headers.TryAddWithoutValidation("Set-Cookie", "SID=session1; path=/");
                return ok;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
        });

        var client = new QBittorrentClient(new HttpClient(handler), Options());
        await client.AddAsync(Magnet(), new DownloadOptions { Paused = true });

        var addBody = handler.Bodies[^1];
        Assert.Contains("paused", addBody);
        Assert.Contains("stopped", addBody);
    }

    /// <summary>Records every request/body and replies via a supplied responder.</summary>
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
