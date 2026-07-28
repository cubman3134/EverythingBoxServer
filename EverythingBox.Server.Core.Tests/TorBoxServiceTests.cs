using System.Net;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Debrid.TorBox;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class TorBoxServiceTests
{
    private static TorrentResult Torrent(string hash = "ABC123")
        => new()
        {
            Title = "The Matrix 1999 1080p BluRay",
            ProviderName = "test",
            MagnetUri = new Uri($"magnet:?xt=urn:btih:{hash}"),
            InfoHash = hash,
        };

    private static TorBoxService Service(StubHandler handler, TimeSpan? maxWait = null)
        => new(new HttpClient(handler), new TorBoxOptions
        {
            ApiKey = "key",
            BaseUrl = new Uri("http://torbox.test/v1/api/"),
            MaxWait = maxWait ?? TimeSpan.Zero,
            PollInterval = TimeSpan.FromMilliseconds(5),
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string ReadyList =
        """{"success":true,"data":{"id":55,"hash":"abc","download_finished":true,"download_present":true,"files":[{"id":0,"name":"movie.mkv","size":1234}]}}""";

    [Fact]
    public async Task ResolvesCachedTorrentToDirectLinks()
    {
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/createtorrent")) return Json("""{"success":true,"detail":"found","data":{"torrent_id":55,"hash":"abc"}}""");
            if (path.EndsWith("/mylist")) return Json(ReadyList);
            if (path.EndsWith("/requestdl")) return Json("""{"success":true,"data":"http://torbox.test/dl/movie.mkv"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.True(result.Success);
        Assert.True(result.Cached);
        Assert.Equal("55", result.TorrentId);
        var link = Assert.Single(result.Links);
        Assert.Equal(new Uri("http://torbox.test/dl/movie.mkv"), link.Url);
        Assert.Equal("movie.mkv", link.FileName);
        Assert.Equal(1234, link.SizeBytes);
    }

    [Fact]
    public async Task UncachedReturnsPendingWhenNotWaiting()
    {
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/createtorrent")) return Json("""{"success":true,"data":{"torrent_id":7}}""");
            if (path.EndsWith("/mylist")) return Json("""{"success":true,"data":{"id":7,"download_finished":false,"download_present":false,"files":[]}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Pending, result.Status);
        Assert.Equal("7", result.TorrentId);
    }

    [Fact]
    public async Task PollsUntilPresentThenResolves()
    {
        var listCalls = 0;
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/createtorrent")) return Json("""{"success":true,"data":{"torrent_id":9}}""");
            if (path.EndsWith("/mylist"))
            {
                listCalls++;
                return listCalls < 2
                    ? Json("""{"success":true,"data":{"id":9,"download_finished":false,"download_present":false,"files":[]}}""")
                    : Json(ReadyList);
            }
            if (path.EndsWith("/requestdl")) return Json("""{"success":true,"data":"http://torbox.test/dl/x.mkv"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler, maxWait: TimeSpan.FromSeconds(2)).ResolveAsync(Torrent());

        Assert.True(result.Success);
        Assert.False(result.Cached); // needed a poll
        Assert.True(listCalls >= 2);
    }

    [Fact]
    public async Task CreateTorrentFailureIsReported()
    {
        var handler = new StubHandler(path =>
            path.EndsWith("/createtorrent")
                ? Json("""{"success":false,"detail":"invalid magnet","data":null}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Failed, result.Status);
        Assert.Contains("invalid magnet", result.Message);
    }

    [Fact]
    public async Task NoMagnetOrHashFailsWithoutNetwork()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var result = await Service(handler).ResolveAsync(Torrent() with { MagnetUri = null, InfoHash = null });

        Assert.Equal(DebridStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BuildsMagnetFromInfoHashWhenNoMagnet()
    {
        string? sentBody = null;
        var handler = new StubHandler((path, body) =>
        {
            if (path.EndsWith("/createtorrent"))
            {
                sentBody = body;
                return Json("""{"success":true,"data":{"torrent_id":1}}""");
            }
            if (path.EndsWith("/mylist")) return Json("""{"success":true,"data":{"id":1,"download_present":true,"files":[]}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler).ResolveAsync(Torrent("FEED") with { MagnetUri = null });

        Assert.True(result.Success);
        Assert.Contains("urn:btih:FEED", sentBody); // literal in multipart body
    }

    [Fact]
    public async Task GetCachedHashesReturnsCachedSubset()
    {
        var handler = new StubHandler(path =>
            path.EndsWith("/checkcached")
                ? Json("""{"success":true,"data":{"abc":{"name":"x","size":1,"hash":"abc"},"def":{"hash":"def"}}}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var cached = await Service(handler).GetCachedHashesAsync(["ABC", "DEF", "GHI"]);

        Assert.Contains("abc", cached);   // case-insensitive
        Assert.Contains("def", cached);
        Assert.DoesNotContain("ghi", cached);
    }

    [Fact]
    public async Task GetCachedHashesEmptyWhenNoneCached()
    {
        var handler = new StubHandler(path =>
            path.EndsWith("/checkcached")
                ? Json("""{"success":true,"data":{}}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        var cached = await Service(handler).GetCachedHashesAsync(["ABC", "DEF"]);

        Assert.Empty(cached);
    }

    [Fact]
    public async Task GetCachedHashesEmptyOnApiError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var cached = await Service(handler).GetCachedHashesAsync(["ABC"]);
        Assert.Empty(cached);
    }

    [Fact]
    public async Task GetCachedHashesSkipsNetworkForEmptyInput()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var cached = await Service(handler).GetCachedHashesAsync([]);

        Assert.Empty(cached);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string, HttpResponseMessage> _responder;

        public StubHandler(Func<string, HttpResponseMessage> byPath)
            => _responder = (path, _) => byPath(path);

        public StubHandler(Func<string, string, HttpResponseMessage> byPathAndBody)
            => _responder = byPathAndBody;

        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request.RequestUri!.AbsolutePath, body);
        }
    }
}
