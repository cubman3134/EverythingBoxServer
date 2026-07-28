using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Providers.Torznab;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class TorznabTests
{
    private const string SampleFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"
             xmlns:atom="http://www.w3.org/2005/Atom"
             xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <title>Test Indexer</title>
            <item>
              <title>The Matrix 1999 1080p BluRay x264-GROUP</title>
              <guid isPermaLink="true">https://tracker.example/details/1</guid>
              <comments>https://tracker.example/details/1</comments>
              <pubDate>Mon, 01 Jan 2024 12:00:00 +0000</pubDate>
              <size>10737418240</size>
              <link>https://tracker.example/download/1.torrent</link>
              <enclosure url="https://tracker.example/download/1.torrent" length="10737418240" type="application/x-bittorrent" />
              <torznab:attr name="seeders" value="120" />
              <torznab:attr name="peers" value="150" />
              <torznab:attr name="infohash" value="ABCDEF0123" />
              <torznab:attr name="category" value="2040" />
            </item>
            <item>
              <title>The Matrix 1999 720p WEB-DL</title>
              <pubDate>Mon, 01 Jan 2024 12:00:00 +0000</pubDate>
              <enclosure url="magnet:?xt=urn:btih:HASH2" type="application/x-bittorrent" />
              <torznab:attr name="seeders" value="50" />
              <torznab:attr name="magneturl" value="magnet:?xt=urn:btih:HASH2" />
            </item>
          </channel>
        </rss>
        """;

    // --- Feed parsing ------------------------------------------------------

    [Fact]
    public void ParsesAllItems()
    {
        var results = TorznabFeedParser.Parse(SampleFeed, "Test");
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("Test", r.ProviderName));
    }

    [Fact]
    public void ParsesTorrentFileItem()
    {
        var item = TorznabFeedParser.Parse(SampleFeed, "Test")[0];

        Assert.Equal("The Matrix 1999 1080p BluRay x264-GROUP", item.Title);
        Assert.Equal(new Uri("https://tracker.example/download/1.torrent"), item.DownloadUrl);
        Assert.Null(item.MagnetUri);
        Assert.Equal(10737418240, item.SizeBytes);
        Assert.Equal(120, item.Seeders);
        Assert.Equal(30, item.Leechers); // peers(150) - seeders(120)
        Assert.Equal("ABCDEF0123", item.InfoHash);
        Assert.Equal(new Uri("https://tracker.example/details/1"), item.DetailsUrl);
        Assert.Contains("2040", item.Categories);
    }

    [Fact]
    public void ParsesMagnetItem()
    {
        var item = TorznabFeedParser.Parse(SampleFeed, "Test")[1];

        Assert.Equal(new Uri("magnet:?xt=urn:btih:HASH2"), item.MagnetUri);
        Assert.Equal(50, item.Seeders);
    }

    [Fact]
    public void MalformedXmlYieldsEmpty()
        => Assert.Empty(TorznabFeedParser.Parse("not xml <<<", "Test"));

    // --- Query building ----------------------------------------------------

    private static readonly TorznabOptions Options = new()
    {
        BaseUrl = new Uri("http://localhost:9696/1/api"),
        ApiKey = "secret",
    };

    [Fact]
    public void BuildsTvSearchUri()
    {
        var request = new TvRequest { Title = "The Office US", Season = 3, Episode = 7 };
        var uri = TorznabQueryBuilder.BuildUri(Options, TorznabQueryBuilder.BuildSearchTerm(request), request);

        Assert.Contains("t=tvsearch", uri.Query);
        Assert.Contains("cat=5000", uri.Query);
        Assert.Contains("season=3", uri.Query);
        Assert.Contains("ep=7", uri.Query);
        Assert.Contains("apikey=secret", uri.Query);
    }

    [Fact]
    public void BuildsMovieUriWithImdbId()
    {
        var request = new MovieRequest
        {
            Title = "The Matrix",
            Year = 1999,
            ExternalIds = new Dictionary<string, string> { ["imdb"] = "tt0133093" },
        };
        var uri = TorznabQueryBuilder.BuildUri(Options, TorznabQueryBuilder.BuildSearchTerm(request), request);

        Assert.Contains("t=movie", uri.Query);
        Assert.Contains("cat=2000", uri.Query);
        Assert.Contains("imdbid=0133093", uri.Query); // "tt" stripped
    }

    [Fact]
    public void RespectsCategoryOverride()
    {
        var options = new TorznabOptions
        {
            BaseUrl = new Uri("http://localhost/api"),
            CategoryOverrides = new Dictionary<MediaType, string> { [MediaType.Movie] = "2040,2045" },
        };
        var request = new MovieRequest { Title = "X" };
        var uri = TorznabQueryBuilder.BuildUri(options, "X", request);

        Assert.Contains("cat=2040%2C2045", uri.Query); // comma encoded
    }

    // --- End-to-end (no network): feed -> grabber -> best ------------------

    [Fact]
    public async Task GrabPicksBestFromFeed()
    {
        var feedResults = TorznabFeedParser.Parse(SampleFeed, "Test");
        var grabber = new TorrentGrabber([new StubProvider(feedResults)]);

        var result = await grabber.GrabAsync(new MovieRequest { Title = "The Matrix", Year = 1999 });

        Assert.True(result.Found);
        // 1080p BluRay beats 720p WEB-DL even though both are relevant.
        Assert.Contains("BluRay", result.Best!.Title);
        Assert.Equal(2, result.Ranked.Count);
    }

    /// <summary>Returns canned results without hitting the network.</summary>
    private sealed class StubProvider(IReadOnlyList<TorrentResult> items) : ITorrentProvider
    {
        public string Name => "stub";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv, MediaType.Music },
        };

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(items);
    }
}
