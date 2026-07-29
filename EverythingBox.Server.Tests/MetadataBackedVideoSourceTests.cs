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

        var types = source.Catalogs.Select(c => c.MediaType).ToArray();
        Assert.Equal(["movie"], types);
    }

    [Fact]
    public void Two_sources_covering_both_types_yield_both_catalogs()
    {
        var source = Source([new StubMetadata("a", ["movie"]), new StubMetadata("b", ["series"])]);

        var types = source.Catalogs.Select(c => c.MediaType).OrderBy(t => t, StringComparer.Ordinal).ToArray();
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
        // RecordingGrabber throws from SearchAsync — reaching it would fail loudly.
        var grabber = new RecordingGrabber();
        var source = Source([new StubMetadata("a", ["movie"], Film("Some Film", 2020))], grabber);

        var item = (await source.SearchAsync("movies", null, Ctx, CancellationToken.None)).Items.Single();

        Assert.Null(await Record.ExceptionAsync(() => source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None)));
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
}
