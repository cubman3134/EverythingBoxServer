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

/// <summary>Records the <see cref="MediaRequest"/> it was resolved with, so a test can
/// assert an item's id round-tripped its catalog's media type into the rebuilt
/// request (C1) rather than always producing a bare GeneralRequest.</summary>
file sealed class RecordingDebrid : IDebridService
{
    public string Name => "recording";
    public MediaRequest? LastRequest { get; private set; }

    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(DebridResult.Resolved(Name, "id", cached: true,
            [new DebridLink("picked.mkv", new Uri("https://example.test/picked.mkv"), 10)]));
    }
}

/// <summary>Always resolves to a fixed set of links, regardless of the request it's
/// handed — B1 needs a debrid stub shaped like TorBoxService's whole-torrent zip plus
/// real files, resolved through an id that decodes with no media type at all.</summary>
file sealed class FixedLinksDebrid(params DebridLink[] links) : IDebridService
{
    public string Name => "fixed";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => Task.FromResult(DebridResult.Resolved(Name, "id", cached: true, links));
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

    [Fact]
    public async Task An_items_id_carries_its_media_type_so_resolving_rebuilds_the_matching_request()
    {
        // C1: an id minted from the series catalog must rebuild a TvRequest at resolve
        // time, not a media-type-less GeneralRequest — otherwise ReleaseStreamResolver's
        // per-file narrowing (season/episode matching) never engages for a season pack.
        var grabber = new RecordingGrabber(Release("Some Series S01"));
        var debrid = new RecordingDebrid();
        var source = Source(grabber, debrid);

        var item = (await source.SearchAsync("series", "some", Ctx, CancellationToken.None)).Items.Single();
        var stream = await source.ResolveAsync(item.Id, 0, Ctx, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.IsType<TvRequest>(debrid.LastRequest);
    }

    [Fact]
    public async Task An_id_with_no_media_type_still_resolves_real_files_not_just_the_archive()
    {
        // B1: an id encoded before the "mt" field existed (or from a source whose
        // protocol string MediaTypeNames doesn't recognize) decodes with MediaType
        // null. Resolving it must not route through the general matcher — which would
        // score the whole-torrent zip as the closest "title" match to the release name
        // and drop every real file, leaving n=1 unreachable (null) and taking away the
        // user's only escape hatch (reject-and-retry).
        var debrid = new FixedLinksDebrid(
            new DebridLink("Some.Release.zip", new Uri("https://example.test/whole.zip"), 3_000_000),
            new DebridLink("Some.Release.S01E01.mkv", new Uri("https://example.test/e1.mkv"), 1_500_000),
            new DebridLink("Some.Release.S01E02.mkv", new Uri("https://example.test/e2.mkv"), 1_500_000));
        var source = Source(new RecordingGrabber(), debrid);

        // A ReleaseRecord JSON payload with no "mt" property — exactly what an id
        // encoded before that field existed looks like. Built by hand rather than via
        // EncodeId/a search round trip, since ReleaseRecord is private and its whole
        // point here is the field's absence.
        const string json = """{"t":"Some Release","p":"test-indexer","h":"0123456789abcdef0123456789abcdef01234567"}""";
        var id = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var n0 = await source.ResolveAsync(id, 0, Ctx, CancellationToken.None);
        var n1 = await source.ResolveAsync(id, 1, Ctx, CancellationToken.None);
        var n2 = await source.ResolveAsync(id, 2, Ctx, CancellationToken.None);

        Assert.NotNull(n0);
        Assert.Equal("https://example.test/e1.mkv", n0!.Url);
        Assert.NotNull(n1);
        Assert.Equal("https://example.test/e2.mkv", n1!.Url);
        Assert.NotNull(n2);
        Assert.Equal("https://example.test/whole.zip", n2!.Url);
    }

    [Fact]
    public async Task An_items_id_omits_the_download_url_when_a_magnet_is_available()
    {
        // I1: DownloadUrl often embeds the indexer manager's own API key and hostname
        // (Prowlarr/Jackett enclosure URLs do). MagnetResolver only ever falls back to
        // DownloadUrl when both MagnetUri and InfoHash are absent, so once a magnet is
        // present the id must not carry the download URL at all — encoding it would leak
        // it to the client and into every request-path log line.
        var release = Release("Some Release 1080p") with
        {
            DownloadUrl = new Uri("https://indexer.example.test/download?apikey=super-secret-key"),
        };
        var grabber = new RecordingGrabber(release);

        var item = (await Source(grabber).SearchAsync("movies", "some", Ctx, CancellationToken.None)).Items.Single();

        Assert.DoesNotContain("super-secret-key", item.Id);
        Assert.DoesNotContain("indexer.example.test", item.Id);
    }
}
