using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core;

namespace EverythingBox.Server.Core.Tests;

file sealed class FixedProvider(params TorrentResult[] results) : ITorrentProvider
{
    public string Name => "fixed";
    public ProviderCapabilities Capabilities { get; } = new()
    {
        SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv },
    };

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TorrentResult>>(results);
}

public class RankedSearchTests
{
    private static TorrentResult Release(string title, int seeders) => new()
    {
        Title = title,
        ProviderName = "fixed",
        InfoHash = title.GetHashCode().ToString("x8").PadLeft(40, '0'),
        MagnetUri = new Uri("magnet:?xt=urn:btih:" + title.GetHashCode().ToString("x8").PadLeft(40, '0')),
        Seeders = seeders,
        SizeBytes = 1_000_000_000,
    };

    private static TorrentGrabber Grabber(RankingOptions ranking, params TorrentResult[] results) =>
        new GrabberBuilder()
            .AddProvider(new FixedProvider(results))
            .Configure(new GrabberOptions { Ranking = ranking })
            .Build();

    [Fact]
    public async Task Ranked_search_orders_best_first()
    {
        var grabber = Grabber(
            new RankingOptions { MinSeeders = 0 },
            Release("Some Movie 2020 720p WEB", 5),
            Release("Some Movie 2020 1080p BluRay", 50));

        var results = await grabber.SearchRankedAsync(new MovieRequest { Title = "Some Movie" }, CancellationToken.None);

        Assert.Equal("Some Movie 2020 1080p BluRay", results[0].Title);
    }

    [Fact]
    public async Task Ranked_search_applies_the_eligibility_gate()
    {
        // This is the whole point: MinSeeders had no effect on a catalog before.
        var grabber = Grabber(
            new RankingOptions { MinSeeders = 10 },
            Release("Some Movie 2020 1080p BluRay", 50),
            Release("Some Movie 2020 720p WEB", 2));

        var results = await grabber.SearchRankedAsync(new MovieRequest { Title = "Some Movie" }, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Some Movie 2020 1080p BluRay", results[0].Title);
    }

    [Fact]
    public async Task Ranked_search_honours_banned_terms()
    {
        var grabber = Grabber(
            new RankingOptions { MinSeeders = 0, BannedTerms = ["CAM"] },
            Release("Some Movie 2020 CAM", 500),
            Release("Some Movie 2020 1080p BluRay", 20));

        var results = await grabber.SearchRankedAsync(new MovieRequest { Title = "Some Movie" }, CancellationToken.None);

        Assert.DoesNotContain(results, r => r.Title.Contains("CAM"));
    }

    [Fact]
    public async Task Plain_search_still_returns_everything_unranked()
    {
        // SearchAsync's contract is unchanged — callers who want raw candidates keep them.
        var grabber = Grabber(
            new RankingOptions { MinSeeders = 10 },
            Release("Some Movie 2020 720p WEB", 2));

        var results = await grabber.SearchAsync(new MovieRequest { Title = "Some Movie" }, CancellationToken.None);

        Assert.Single(results);
    }

    [Fact]
    public async Task Ranked_search_of_nothing_is_empty_not_null()
    {
        var grabber = Grabber(new RankingOptions { MinSeeders = 0 });
        var results = await grabber.SearchRankedAsync(new MovieRequest { Title = "Nothing" }, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Ranked_search_applies_size_bounds()
    {
        // MinSizeBytes and MaxSizeBytes must be enforced; a refactor could silently drop them.
        var tooSmall = new TorrentResult
        {
            Title = "Some Movie 2020 - Tiny",
            ProviderName = "fixed",
            InfoHash = "0000000000000000000000000000000000000000",
            MagnetUri = new Uri("magnet:?xt=urn:btih:0000000000000000000000000000000000000000"),
            Seeders = 50,
            SizeBytes = 100_000_000, // 100 MB
        };
        var inRange = new TorrentResult
        {
            Title = "Some Movie 2020 720p",
            ProviderName = "fixed",
            InfoHash = "1111111111111111111111111111111111111111",
            MagnetUri = new Uri("magnet:?xt=urn:btih:1111111111111111111111111111111111111111"),
            Seeders = 50,
            SizeBytes = 2_000_000_000, // 2 GB
        };
        var tooLarge = new TorrentResult
        {
            Title = "Some Movie 2020 - Huge",
            ProviderName = "fixed",
            InfoHash = "2222222222222222222222222222222222222222",
            MagnetUri = new Uri("magnet:?xt=urn:btih:2222222222222222222222222222222222222222"),
            Seeders = 50,
            SizeBytes = 20_000_000_000, // 20 GB
        };

        var grabber = Grabber(
            new RankingOptions { MinSeeders = 0, MinSizeBytes = 500_000_000, MaxSizeBytes = 10_000_000_000 },
            tooSmall, inRange, tooLarge);

        var results = await grabber.SearchRankedAsync(new MovieRequest { Title = "Some Movie" }, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Some Movie 2020 720p", results[0].Title);
    }
}
