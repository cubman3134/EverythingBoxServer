using System.Net;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Debrid.RealDebrid;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class RealDebridServiceTests
{
    private static TorrentResult Torrent(string hash = "ABC123")
        => new()
        {
            Title = "The Matrix 1999 1080p BluRay",
            ProviderName = "test",
            MagnetUri = new Uri($"magnet:?xt=urn:btih:{hash}"),
            InfoHash = hash,
        };

    private static RealDebridService Service(StubHandler handler, TimeSpan? maxWait = null, TimeSpan? poll = null)
        => new(new HttpClient(handler), new RealDebridOptions
        {
            ApiToken = "token",
            BaseUrl = new Uri("http://rd.test/rest/1.0/"),
            MaxWait = maxWait ?? TimeSpan.Zero,
            PollInterval = poll ?? TimeSpan.FromMilliseconds(5),
        });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task ResolvesCachedTorrentToDirectLinks()
    {
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/addMagnet")) return Json("""{"id":"T1","uri":"x"}""");
            if (path.Contains("/selectFiles/")) return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.Contains("/info/")) return Json("""{"status":"downloaded","links":["http://rd.test/r/1"]}""");
            if (path.EndsWith("/unrestrict/link")) return Json("""{"download":"http://rd.test/d/movie.mkv","filename":"movie.mkv","filesize":1234}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.True(result.Success);
        Assert.True(result.Cached);
        Assert.Equal("T1", result.TorrentId);
        var link = Assert.Single(result.Links);
        Assert.Equal(new Uri("http://rd.test/d/movie.mkv"), link.Url);
        Assert.Equal("movie.mkv", link.FileName);
        Assert.Equal(1234, link.SizeBytes);
    }

    [Fact]
    public async Task UncachedReturnsPendingWhenNotWaiting()
    {
        var infoCalls = 0;
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/addMagnet")) return Json("""{"id":"T2"}""");
            if (path.Contains("/selectFiles/")) return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.Contains("/info/"))
            {
                infoCalls++;
                return infoCalls == 1
                    ? Json("""{"status":"waiting_files_selection","files":[{"id":1,"path":"/movie.mkv","bytes":100}]}""")
                    : Json("""{"status":"downloading","links":[]}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Pending, result.Status);
        Assert.Equal("T2", result.TorrentId);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task PollsUntilDownloadedThenResolves()
    {
        var infoCalls = 0;
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/addMagnet")) return Json("""{"id":"T3"}""");
            if (path.Contains("/selectFiles/")) return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.Contains("/info/"))
            {
                infoCalls++;
                return infoCalls switch
                {
                    1 => Json("""{"status":"waiting_files_selection","files":[{"id":1,"path":"/x.mkv","bytes":9}]}"""),
                    2 => Json("""{"status":"downloading","links":[]}"""),
                    _ => Json("""{"status":"downloaded","links":["http://rd.test/r/1"]}"""),
                };
            }
            if (path.EndsWith("/unrestrict/link")) return Json("""{"download":"http://rd.test/d/x.mkv","filename":"x.mkv","filesize":9}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler, maxWait: TimeSpan.FromSeconds(2), poll: TimeSpan.FromMilliseconds(5))
            .ResolveAsync(Torrent());

        Assert.True(result.Success);
        Assert.False(result.Cached); // needed a poll, so not instantly available
        Assert.True(infoCalls >= 3);
    }

    [Fact]
    public async Task SelectsOnlyRequestedEpisodeFromSeasonPack()
    {
        var infoCalls = 0;
        var handler = new StubHandler((path, _) =>
        {
            if (path.EndsWith("/addMagnet")) return Json("""{"id":"T6"}""");
            if (path.Contains("/selectFiles/")) return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.Contains("/info/"))
            {
                infoCalls++;
                return infoCalls == 1
                    ? Json("""{"status":"waiting_files_selection","files":[{"id":1,"path":"/Show.S01E01.mkv","bytes":100},{"id":2,"path":"/Show.S01E02.mkv","bytes":110},{"id":3,"path":"/Show.S01E03.mkv","bytes":105}]}""")
                    : Json("""{"status":"downloaded","links":["http://rd.test/r/ep2"]}""");
            }
            if (path.EndsWith("/unrestrict/link")) return Json("""{"download":"http://rd.test/d/Show.S01E02.mkv","filename":"Show.S01E02.mkv","filesize":110}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var request = new TvRequest { Title = "Show", Season = 1, Episode = 2 };
        var result = await Service(handler).ResolveAsync(Torrent(), request);

        Assert.True(result.Success);
        var link = Assert.Single(result.Links);
        Assert.Equal("Show.S01E02.mkv", link.FileName);

        // Only the matching file's id was selected — not "all".
        var select = handler.Log.First(e => e.Path.Contains("/selectFiles/"));
        Assert.Equal("files=2", select.Body);
    }

    [Fact]
    public async Task TerminalErrorStatusFails()
    {
        var handler = new StubHandler(path =>
        {
            if (path.EndsWith("/addMagnet")) return Json("""{"id":"T4"}""");
            if (path.Contains("/selectFiles/")) return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.Contains("/info/")) return Json("""{"status":"dead","links":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Failed, result.Status);
        Assert.Contains("dead", result.Message);
    }

    [Fact]
    public async Task AddMagnetErrorIsReported()
    {
        var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"bad_token"}""") });

        var result = await Service(handler).ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Failed, result.Status);
        Assert.Contains("addMagnet", result.Message);
    }

    [Fact]
    public async Task NoMagnetOrHashFailsWithoutNetwork()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var noLink = Torrent() with { MagnetUri = null, InfoHash = null };

        var result = await Service(handler).ResolveAsync(noLink);

        Assert.Equal(DebridStatus.Failed, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task BuildsMagnetFromInfoHashWhenNoMagnet()
    {
        string? sentMagnet = null;
        var handler = new StubHandler((path, body) =>
        {
            if (path.EndsWith("/addMagnet"))
            {
                sentMagnet = body;
                return Json("""{"id":"T5"}""");
            }
            if (path.Contains("/selectFiles/")) return new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") };
            if (path.Contains("/info/")) return Json("""{"status":"downloaded","links":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var hashOnly = Torrent("FEED") with { MagnetUri = null };
        var result = await Service(handler).ResolveAsync(hashOnly);

        Assert.True(result.Success);
        Assert.Contains("urn%3Abtih%3AFEED", sentMagnet); // form-encoded magnet built from hash
    }

    /// <summary>Routes responses by request path (and optionally body), counting calls.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string, HttpResponseMessage> _responder;

        public StubHandler(Func<string, HttpResponseMessage> byPath)
            => _responder = (path, _) => byPath(path);

        public StubHandler(Func<string, string, HttpResponseMessage> byPathAndBody)
            => _responder = byPathAndBody;

        public int Calls { get; private set; }

        public List<(string Path, string Body)> Log { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Log.Add((path, body));
            return _responder(path, body);
        }
    }
}
