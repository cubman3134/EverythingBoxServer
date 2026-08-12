using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

file sealed class StubMetadata(string name, string[] types, params MetadataItem[] items) : IMetadataSource
{
    public string Name => name;
    public IReadOnlyList<string> SupportedMediaTypes { get; } = types;
    public string? LastQuery { get; private set; }
    public string? LastMediaType { get; private set; }

    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
    {
        LastMediaType = mediaType;
        LastQuery = query;
        return Task.FromResult<IReadOnlyList<MetadataItem>>(items);
    }
}

file sealed class ThrowingMetadata : IMetadataSource
{
    public string Name => "throwing";
    public IReadOnlyList<string> SupportedMediaTypes { get; } = ["movie"];
    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => throw new HttpRequestException("metadata upstream is down");
}

file sealed class ThrowingEpisodesMetadata : IMetadataSource
{
    public string Name => "throwing-episodes";
    public IReadOnlyList<string> SupportedMediaTypes { get; } = ["series"];

    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>([new MetadataItem("s1", "Some Show", "series")]);

    public Task<IReadOnlyList<MetadataEpisode>> EpisodesAsync(string seriesId, CancellationToken ct)
        => throw new HttpRequestException("metadata upstream is down");
}

file sealed class EpisodeMetadata(MetadataItem series, MetadataEpisode[] episodes) : IMetadataSource
{
    public string Name => "episodes";
    public IReadOnlyList<string> SupportedMediaTypes { get; } = ["series"];

    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>([series]);

    public Task<IReadOnlyList<MetadataEpisode>> EpisodesAsync(string seriesId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataEpisode>>(episodes);
}

file sealed class RecordingGrabber(params TorrentResult[] results) : ITorrentGrabber
{
    public MediaRequest? LastRequest { get; private set; }

    public Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("browse should use the ranked search");

    public Task<IReadOnlyList<TorrentResult>> SearchRankedAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult<IReadOnlyList<TorrentResult>>(results);
    }
}

/// <summary>Resolves a fixed set of debrid links keyed by release title — the test
/// harness passes a null debrid in most of this file's tests (short-circuits before
/// file narrowing), so the walk tests need a stub that actually returns links.</summary>
file sealed class KeyedDebrid(IReadOnlyDictionary<string, DebridResult> byTitle) : IDebridService
{
    public string Name => "keyed";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => Task.FromResult(byTitle[torrent.Title]);
}

/// <summary>Counts debrid round trips and always fails — the I1 cap test's stand-in
/// for "an expired/wrong debrid API key fails every candidate identically", the most
/// common real-world way an unbounded candidate walk turns into hundreds of debrid
/// calls for a single request.</summary>
file sealed class CountingFailingDebrid : IDebridService
{
    public string Name => "counting";
    public int CallCount { get; private set; }

    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(DebridResult.Failed("counting", "always fails"));
    }
}

public class MetadataBackedVideoSourceTests
{
    private static readonly SourceContext Ctx = new();

    private static MetadataBackedVideoSource Source(
        IEnumerable<IMetadataSource> metadata, ITorrentGrabber? grabber = null) =>
        new(metadata.ToArray(),
            grabber ?? new RecordingGrabber(),
            new ReleaseStreamResolver(null, NullLogger<ReleaseStreamResolver>.Instance),
            NullLogger<MetadataBackedVideoSource>.Instance);

    private static MetadataItem Film(string title, int year) => new("m1", title, "movie", Year: year);

    private static IMetadataSource StubWithEpisodes(MetadataItem series, params MetadataEpisode[] episodes)
        => new EpisodeMetadata(series, episodes);

    [Fact]
    public void Its_key_is_meta_and_needs_no_special_routing()
        => Assert.Equal("meta", Source([new StubMetadata("s", ["movie"])]).Key);

    [Fact]
    public void With_no_metadata_source_it_declares_no_catalogs()
    {
        // A manifest must not promise a shelf nothing can fill.
        Assert.Empty(Source([]).Catalogs);
    }

    [Fact]
    public void It_declares_only_the_types_a_registered_source_supports()
    {
        var source = Source([new StubMetadata("s", ["movie"])]);

        var types = source.Catalogs.Select(c => c.Kind).ToArray();
        Assert.Equal(["movie"], types);
    }

    [Fact]
    public void Two_sources_covering_both_types_yield_both_catalogs()
    {
        var source = Source([new StubMetadata("a", ["movie"]), new StubMetadata("b", ["series"])]);

        var types = source.Catalogs.Select(c => c.Kind).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(["movie", "series"], types);
    }

    [Fact]
    public void It_declares_no_media_types_because_movie_and_series_are_built_in()
    {
        var source = Source([new StubMetadata("a", ["movie"]), new StubMetadata("b", ["series"])]);
        Assert.Empty(source.MediaTypes);
    }

    [Fact]
    public async Task Browsing_asks_every_source_supporting_that_type()
    {
        var a = new StubMetadata("a", ["movie"], Film("First", 2001));
        var b = new StubMetadata("b", ["movie"], Film("Second", 2002));

        var catalog = await Source([a, b]).SearchAsync("movies", null, Ctx, CancellationToken.None);

        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal("movie", a.LastMediaType);
    }

    [Fact]
    public async Task A_query_is_passed_through_to_the_metadata_source()
    {
        var a = new StubMetadata("a", ["movie"], Film("First", 2001));

        await Source([a]).SearchAsync("movies", "first", Ctx, CancellationToken.None);

        Assert.Equal("first", a.LastQuery);
    }

    [Fact]
    public async Task A_metadata_source_that_throws_is_skipped_and_the_others_still_answer()
    {
        var healthy = new StubMetadata("healthy", ["movie"], Film("Survivor", 2003));

        var catalog = await Source([new ThrowingMetadata(), healthy])
            .SearchAsync("movies", null, Ctx, CancellationToken.None);

        Assert.Equal("Survivor", Assert.Single(catalog.Items).Title);
    }

    [Fact]
    public async Task An_item_carries_its_title_year_and_poster()
    {
        var item = new MetadataItem("m1", "Some Film", "movie", Year: 2020, PosterUrl: "https://example.test/p.jpg");

        var catalog = await Source([new StubMetadata("a", ["movie"], item)])
            .SearchAsync("movies", null, Ctx, CancellationToken.None);

        var got = Assert.Single(catalog.Items);
        Assert.Equal("Some Film", got.Title);
        Assert.Contains("2020", got.Subtitle);
        Assert.Equal("https://example.test/p.jpg", got.ThumbnailUrl);
    }

    [Fact]
    public async Task Resolving_a_movie_searches_the_pipeline_for_its_title_and_year()
    {
        var grabber = new RecordingGrabber();
        var source = Source([new StubMetadata("a", ["movie"], Film("Some Film", 2020))], grabber);

        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();
        await source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None);

        var request = Assert.IsType<MovieRequest>(grabber.LastRequest);
        Assert.Equal("Some Film", request.Title);
        Assert.Equal(2020, request.Year);
    }

    [Fact]
    public async Task Resolving_uses_the_ranked_search_not_the_raw_one()
    {
        // RecordingGrabber throws from SearchAsync — but ResolveAsync catches whatever
        // the grabber throws and returns null, so "no exception escaped" alone does NOT
        // prove SearchRankedAsync was used: the mutation SearchRankedAsync -> SearchAsync
        // would hit the throw, get caught, and return null too, looking identical from
        // outside. Assert LastRequest was actually recorded, the same way
        // IndexerSearchSourceTests.Searching_uses_the_ranked_pipeline_so_Ranking_config_applies
        // does for its own SearchAsync/SearchRankedAsync split.
        var grabber = new RecordingGrabber();
        var source = Source([new StubMetadata("a", ["movie"], Film("Some Film", 2020))], grabber);

        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        Assert.Null(await Record.ExceptionAsync(() => source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None)));
        Assert.NotNull(grabber.LastRequest);
    }

    [Theory]
    [InlineData("not-base64url!!")]
    [InlineData("")]
    [InlineData("YWJj")]
    public async Task A_malformed_id_resolves_to_null_rather_than_throwing(string id)
        => Assert.Null(await Source([new StubMetadata("a", ["movie"])]).ResolveAsync(id, 0, Ctx, CancellationToken.None));

    [Fact]
    public async Task An_unknown_catalog_id_yields_an_empty_catalog()
    {
        var catalog = await Source([new StubMetadata("a", ["movie"], Film("First", 2001))])
            .SearchAsync("nosuchcatalog", null, Ctx, CancellationToken.None);

        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task A_series_item_is_marked_expandable()
    {
        var series = new MetadataItem("s1", "Some Show", "series", Year: 2019);
        var catalog = await Source([new StubMetadata("a", ["series"], series)])
            .SearchAsync("series", null, Ctx, CancellationToken.None);

        Assert.True(Assert.Single(catalog.Items).Expandable);
    }

    [Fact]
    public async Task A_movie_item_is_not_expandable()
    {
        var catalog = await Source([new StubMetadata("a", ["movie"], Film("Some Film", 2020))])
            .SearchAsync("movies", null, Ctx, CancellationToken.None);

        Assert.False(Assert.Single(catalog.Items).Expandable);
    }

    [Fact]
    public async Task Expanding_a_series_lists_its_episodes()
    {
        var stub = StubWithEpisodes(
            new MetadataItem("s1", "Some Show", "series"),
            new MetadataEpisode(1, 1, "Pilot"),
            new MetadataEpisode(1, 2, "Second"));

        var source = Source([stub]);
        var series = (await source.SearchAsync("series", null, Ctx, CancellationToken.None)).Items.Single();

        var detail = await source.DetailAsync(series.Id, Ctx, CancellationToken.None);

        Assert.Equal(2, detail.Items.Count);
        Assert.Contains("Pilot", detail.Items[0].Title);
    }

    [Fact]
    public async Task Resolving_an_episode_asks_for_that_season_and_episode()
    {
        var grabber = new RecordingGrabber();
        var stub = StubWithEpisodes(
            new MetadataItem("s1", "Some Show", "series"),
            new MetadataEpisode(3, 2, "The One"));

        var source = Source([stub], grabber);
        var series = (await source.SearchAsync("series", null, Ctx, CancellationToken.None)).Items.Single();
        var episode = (await source.DetailAsync(series.Id, Ctx, CancellationToken.None)).Items.Single();

        await source.ResolveAsync(episode.Id, 0, Ctx, CancellationToken.None);

        var request = Assert.IsType<TvRequest>(grabber.LastRequest);
        Assert.Equal("Some Show", request.Title);
        Assert.Equal(3, request.Season);
        Assert.Equal(2, request.Episode);
    }

    [Fact]
    public async Task A_series_that_reports_no_episodes_expands_to_an_empty_detail()
    {
        // A source declaring "series" but not implementing EpisodesAsync gets the default.
        var source = Source([new StubMetadata("a", ["series"], new MetadataItem("s1", "Some Show", "series"))]);
        var series = (await source.SearchAsync("series", null, Ctx, CancellationToken.None)).Items.Single();

        var detail = await source.DetailAsync(series.Id, Ctx, CancellationToken.None);

        Assert.Empty(detail.Items);
    }

    [Fact]
    public async Task Expanding_something_that_is_not_a_series_is_empty_rather_than_an_error()
    {
        var source = Source([new StubMetadata("a", ["movie"], Film("Some Film", 2020))]);
        var movie = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        Assert.Empty((await source.DetailAsync(movie.Id, Ctx, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task A_metadata_source_that_throws_while_expanding_is_contained()
    {
        var source = Source([new ThrowingEpisodesMetadata()]);
        var series = (await source.SearchAsync("series", null, Ctx, CancellationToken.None)).Items.Single();

        Assert.Empty((await source.DetailAsync(series.Id, Ctx, CancellationToken.None)).Items);
    }

    // --- ?n= walks files within a release before moving to the next candidate ---

    [Fact]
    public async Task Resolving_walks_files_within_a_release_before_moving_to_the_next_candidate()
    {
        var candidateA = new TorrentResult { Title = "Release A", ProviderName = "test-indexer" };
        var candidateB = new TorrentResult { Title = "Release B", ProviderName = "test-indexer" };
        var grabber = new RecordingGrabber(candidateA, candidateB);
        var debrid = new KeyedDebrid(new Dictionary<string, DebridResult>
        {
            ["Release A"] = DebridResult.Resolved("keyed", "id-a", cached: true,
            [
                new DebridLink("a1.mkv", new Uri("https://example.test/a1.mkv"), 100),
                new DebridLink("a2.mkv", new Uri("https://example.test/a2.mkv"), 100),
            ]),
            ["Release B"] = DebridResult.Resolved("keyed", "id-b", cached: true,
            [
                new DebridLink("b1.mkv", new Uri("https://example.test/b1.mkv"), 100),
            ]),
        });
        var resolver = new ReleaseStreamResolver(debrid, NullLogger<ReleaseStreamResolver>.Instance);
        var source = new MetadataBackedVideoSource(
            [new StubMetadata("a", ["movie"], Film("Some Film", 2020))],
            grabber, resolver, NullLogger<MetadataBackedVideoSource>.Instance);
        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        var n0 = await source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None);
        var n1 = await source.ResolveAsync(item.Id, 1, Ctx, CancellationToken.None);
        var n2 = await source.ResolveAsync(item.Id, 2, Ctx, CancellationToken.None);

        // n=0 and n=1 are two different files from the SAME release (A) — only once
        // both are exhausted does n=2 fall through to release B.
        Assert.Equal("https://example.test/a1.mkv", n0!.Url);
        Assert.Equal("https://example.test/a2.mkv", n1!.Url);
        Assert.Equal("https://example.test/b1.mkv", n2!.Url);
    }

    [Fact]
    public async Task Walking_past_every_candidates_files_returns_null_rather_than_throwing()
    {
        var candidate = new TorrentResult { Title = "Only Release", ProviderName = "test-indexer" };
        var grabber = new RecordingGrabber(candidate);
        var debrid = new KeyedDebrid(new Dictionary<string, DebridResult>
        {
            ["Only Release"] = DebridResult.Resolved("keyed", "id", cached: true,
            [
                new DebridLink("only.mkv", new Uri("https://example.test/only.mkv"), 100),
            ]),
        });
        var resolver = new ReleaseStreamResolver(debrid, NullLogger<ReleaseStreamResolver>.Instance);
        var source = new MetadataBackedVideoSource(
            [new StubMetadata("a", ["movie"], Film("Some Film", 2020))],
            grabber, resolver, NullLogger<MetadataBackedVideoSource>.Instance);
        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        SourceStream? stream = null;
        var thrown = await Record.ExceptionAsync(async () =>
            stream = await source.ResolveAsync(item.Id, 5, Ctx, CancellationToken.None));

        Assert.Null(thrown);
        Assert.Null(stream);
    }

    // --- I1: the candidate walk is bounded, so one misconfiguration (or a season pack
    // that narrows to zero files) can't turn a single request into hundreds of debrid
    // round trips ---

    [Fact]
    public async Task Resolving_caps_how_many_candidates_it_will_walk_through_debrid()
    {
        // Every candidate fails identically — the same shape as an expired/wrong debrid
        // API key, the most ordinary real-world cause. 50 is a fixed literal, not tied
        // to MaxCandidatesToResolve, deliberately: it must stay far bigger than the cap
        // even if the cap is later raised, so this test still bites if the cap is ever
        // widened enough to matter (verified by temporarily setting the cap far above
        // 50 and confirming this test then fails, expecting the smaller cap-based count
        // but observing all 50 candidates resolved instead).
        const int candidateCount = 50;
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(i => new TorrentResult { Title = $"Release {i}", ProviderName = "test-indexer" })
            .ToArray();
        var grabber = new RecordingGrabber(candidates);
        var debrid = new CountingFailingDebrid();
        var resolver = new ReleaseStreamResolver(debrid, NullLogger<ReleaseStreamResolver>.Instance);
        var source = new MetadataBackedVideoSource(
            [new StubMetadata("a", ["movie"], Film("Some Film", 2020))],
            grabber, resolver, NullLogger<MetadataBackedVideoSource>.Instance);
        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        var result = await source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(MetadataBackedVideoSource.MaxCandidatesToResolve, debrid.CallCount);
    }

    [Fact]
    public async Task The_cap_does_not_disturb_the_normal_walk_across_two_candidates()
    {
        // Same shape as Resolving_walks_files_within_a_release_before_moving_to_the_next_candidate,
        // but explicit about why it also matters for I1: two candidates sit far under
        // MaxCandidatesToResolve, so n=0/n=1/n=2 must behave exactly as before the cap
        // was introduced — the cap must not make the ordinary case walk fewer files.
        var candidateA = new TorrentResult { Title = "Release A", ProviderName = "test-indexer" };
        var candidateB = new TorrentResult { Title = "Release B", ProviderName = "test-indexer" };
        var grabber = new RecordingGrabber(candidateA, candidateB);
        var debrid = new KeyedDebrid(new Dictionary<string, DebridResult>
        {
            ["Release A"] = DebridResult.Resolved("keyed", "id-a", cached: true,
            [
                new DebridLink("a1.mkv", new Uri("https://example.test/a1.mkv"), 100),
                new DebridLink("a2.mkv", new Uri("https://example.test/a2.mkv"), 100),
            ]),
            ["Release B"] = DebridResult.Resolved("keyed", "id-b", cached: true,
            [
                new DebridLink("b1.mkv", new Uri("https://example.test/b1.mkv"), 100),
            ]),
        });
        var resolver = new ReleaseStreamResolver(debrid, NullLogger<ReleaseStreamResolver>.Instance);
        var source = new MetadataBackedVideoSource(
            [new StubMetadata("a", ["movie"], Film("Some Film", 2020))],
            grabber, resolver, NullLogger<MetadataBackedVideoSource>.Instance);
        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        var n0 = await source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None);
        var n1 = await source.ResolveAsync(item.Id, 1, Ctx, CancellationToken.None);
        var n2 = await source.ResolveAsync(item.Id, 2, Ctx, CancellationToken.None);

        Assert.Equal("https://example.test/a1.mkv", n0!.Url);
        Assert.Equal("https://example.test/a2.mkv", n1!.Url);
        Assert.Equal("https://example.test/b1.mkv", n2!.Url);
    }

    // --- I3: an item whose own MediaType disagrees with the catalog it's browsed
    // under is dropped, not silently mis-shelved ---

    [Fact]
    public async Task An_item_whose_media_type_does_not_match_the_browsed_catalog_is_excluded()
    {
        var mismatched = new MetadataItem("s1", "Wrongly Typed", "series");
        var source = Source([new StubMetadata("a", ["movie"], mismatched)]);

        var catalog = await source.SearchAsync("movies", null, Ctx, CancellationToken.None);

        Assert.Empty(catalog.Items);
    }
}
