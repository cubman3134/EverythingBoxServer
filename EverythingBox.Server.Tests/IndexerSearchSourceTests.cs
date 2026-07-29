using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Records the request it was handed, so a test can assert the catalog's
/// media type was translated into the right concrete request.</summary>
file sealed class RecordingGrabber(params TorrentResult[] results) : ITorrentGrabber
{
    public MediaRequest? LastRequest { get; private set; }

    public Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("IndexerSearchSource lists candidates; it does not auto-pick.");

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult<IReadOnlyList<TorrentResult>>(results);
    }
}

file sealed class ThrowingGrabber : ITorrentGrabber
{
    public Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => throw new HttpRequestException("indexer unreachable");

    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => throw new HttpRequestException("indexer unreachable");
}

public class IndexerSearchSourceTests
{
    private static TorrentResult Release(string title) => new()
    {
        Title = title,
        ProviderName = "test-indexer",
        InfoHash = "0123456789abcdef0123456789abcdef01234567",
        MagnetUri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
        SizeBytes = 1_500_000_000,
        Seeders = 42,
    };

    /// <summary>No debrid by default; pass one when a test needs resolution to succeed.</summary>
    private static IndexerSearchSource Source(ITorrentGrabber grabber, IDebridService? debrid = null) =>
        new(grabber,
            new ReleaseStreamResolver(debrid, NullLogger<ReleaseStreamResolver>.Instance),
            NullLogger<IndexerSearchSource>.Instance);

    private sealed class StubDebrid : IDebridService
    {
        public string Name => "stub";
        public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
            => Task.FromResult(DebridResult.Resolved("stub", "id", cached: true,
                [new DebridLink("picked.mkv", new Uri("https://example.test/picked.mkv"), 10)]));
    }

    private static readonly SourceContext Ctx = new();

    [Fact]
    public void Its_key_is_idx_and_needs_no_special_routing()
    {
        Assert.Equal("idx", Source(new RecordingGrabber()).Key);
    }

    [Fact]
    public void Declares_a_catalog_per_searchable_media_type()
    {
        var types = Source(new RecordingGrabber()).Catalogs
            .Select(c => c.MediaType)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["audiobook", "book", "comic", "movie", "music", "series"], types);
    }

    [Fact]
    public void Declares_a_media_type_for_every_non_builtin_catalog_it_offers()
    {
        var source = Source(new RecordingGrabber());

        var declared = source.MediaTypes.Select(t => t.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var needed = source.Catalogs
            .Select(c => c.MediaType)
            .Where(t => t is not ("movie" or "series"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A catalog whose type the client does not know natively and which declares no
        // descriptor renders as an unlabelled shelf.
        Assert.True(needed.IsSubsetOf(declared),
            "Undeclared media types: " + string.Join(", ", needed.Except(declared)));
    }

    [Fact]
    public void Does_not_declare_movie_or_series_which_the_client_knows_natively()
    {
        var declared = Source(new RecordingGrabber()).MediaTypes.Select(t => t.Type).ToArray();
        Assert.DoesNotContain("movie", declared);
        Assert.DoesNotContain("series", declared);
    }

    [Fact]
    public async Task Without_a_query_it_returns_an_empty_catalog_rather_than_searching()
    {
        // These are search-only shelves — there is nothing to browse, and firing a
        // blank search at every configured indexer on every catalog open is wasteful.
        var grabber = new RecordingGrabber(Release("Something"));

        var catalog = await Source(grabber).SearchAsync("movies", null, Ctx, CancellationToken.None);

        Assert.Empty(catalog.Items);
        Assert.Null(grabber.LastRequest);
    }

    [Theory]
    [InlineData("movies", typeof(MovieRequest))]
    [InlineData("series", typeof(TvRequest))]
    [InlineData("music", typeof(MusicRequest))]
    [InlineData("audiobooks", typeof(AudiobookRequest))]
    [InlineData("books", typeof(BookRequest))]
    [InlineData("comics", typeof(ComicRequest))]
    public async Task A_query_reaches_the_grabber_as_a_request_of_the_catalogs_type(string catalogId, Type expected)
    {
        // This is the assertion that catches a wrong vocabulary mapping — the whole
        // reason MediaTypeNames exists. Searching the series shelf must not send a
        // MovieRequest, which would silently return the wrong Torznab category.
        var grabber = new RecordingGrabber();

        await Source(grabber).SearchAsync(catalogId, "some title", Ctx, CancellationToken.None);

        Assert.NotNull(grabber.LastRequest);
        Assert.IsType(expected, grabber.LastRequest);
        Assert.Equal("some title", grabber.LastRequest!.Title);
    }

    [Fact]
    public async Task Each_release_becomes_an_item_carrying_its_title_and_stats()
    {
        var grabber = new RecordingGrabber(Release("Some Release 1080p"));

        var catalog = await Source(grabber).SearchAsync("movies", "some", Ctx, CancellationToken.None);

        var item = Assert.Single(catalog.Items);
        Assert.Equal("Some Release 1080p", item.Title);
        Assert.Equal("movie", item.MediaType);
        Assert.Contains("42", item.Subtitle);          // seeders are worth seeing
        Assert.False(string.IsNullOrWhiteSpace(item.Id));
    }

    [Fact]
    public async Task An_items_id_round_trips_back_to_a_resolvable_release()
    {
        // The id is opaque to the host but must survive the trip to the client and back.
        // A debrid stub is supplied so a successful decode produces an actual stream —
        // without one, a decode failure and a decode success both return null and the
        // test would pass either way.
        var grabber = new RecordingGrabber(Release("Some Release 1080p"));
        var source = Source(grabber, new StubDebrid());

        var item = (await source.SearchAsync("movies", "some", Ctx, CancellationToken.None)).Items.Single();
        var stream = await source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/picked.mkv", stream!.Url);
    }

    [Theory]
    [InlineData("not-base64url!!")]
    [InlineData("")]
    [InlineData("YWJj")]                 // valid base64url, not our payload
    public async Task A_malformed_id_resolves_to_null_rather_than_throwing(string id)
    {
        // Ids arrive from the client and are never trusted.
        var source = Source(new RecordingGrabber());
        Assert.Null(await source.ResolveAsync(id, 0, Ctx, CancellationToken.None));
    }

    [Fact]
    public async Task A_grabber_that_throws_yields_an_empty_catalog()
    {
        // An unreachable indexer must degrade to an empty shelf, not a failed request.
        var catalog = await Source(new ThrowingGrabber()).SearchAsync("movies", "some", Ctx, CancellationToken.None);
        Assert.Empty(catalog.Items);
    }

    [Fact]
    public async Task An_unknown_catalog_id_yields_an_empty_catalog()
    {
        var grabber = new RecordingGrabber(Release("Something"));

        var catalog = await Source(grabber).SearchAsync("nosuchcatalog", "some", Ctx, CancellationToken.None);

        Assert.Empty(catalog.Items);
        Assert.Null(grabber.LastRequest);
    }

    [Fact]
    public async Task Detail_is_empty_because_a_release_does_not_expand()
    {
        var grabber = new RecordingGrabber(Release("Something"));
        var source = Source(grabber);
        var item = (await source.SearchAsync("movies", "some", Ctx, CancellationToken.None)).Items.Single();

        var detail = await source.DetailAsync(item.Id, Ctx, CancellationToken.None);

        Assert.Empty(detail.Items);
    }
}
