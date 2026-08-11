using EverythingBox.Server.Abstractions;
using EverythingBox.Server.LocalLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class LocalLibrarySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-lib-" + Guid.NewGuid().ToString("N"));

    public LocalLibrarySourceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "Some.Movie.2019.1080p.BluRay.mkv"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_root, "Another Film (2020).mp4"), new byte[] { 5, 6, 7, 8 });
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a movie");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } GC.SuppressFinalize(this); }

    private LocalLibrarySource Movies(params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, [], null, NullLogger<LocalLibrarySource>.Instance);

    private sealed class SpyCache : EverythingBox.Server.Abstractions.IResolverCache
    {
        public int Gets, Sets;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _s = new();
        public Task<string?> GetAsync(string k, CancellationToken ct = default) { Gets++; return Task.FromResult(_s.TryGetValue(k, out var v) ? v : null); }
        public Task SetAsync(string k, string v, CancellationToken ct = default) { Sets++; _s[k] = v; return Task.CompletedTask; }
    }

    private LocalLibrarySource CachedMovies(EverythingBox.Server.Abstractions.IResolverCache cache, params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, [], cache, NullLogger<LocalLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    [Fact]
    public void No_configured_roots_declares_no_catalog()
        => Assert.Empty(new LocalLibrarySource([], [], null, NullLogger<LocalLibrarySource>.Instance).Catalogs);

    [Fact]
    public void A_configured_root_declares_the_movies_catalog()
    {
        var c = Assert.Single(Movies().Catalogs);
        Assert.Equal("movies", c.Id);
        Assert.Equal("movie", c.MediaType);
    }

    [Fact]
    public async Task Scans_only_video_files_and_titles_them_from_the_filename()
    {
        var catalog = await Movies().SearchAsync("movies", null, Ctx(), default);
        Assert.Equal(2, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.Equal("movie", i.MediaType));
        Assert.All(catalog.Items, i => Assert.False(i.Expandable));
        Assert.Contains(catalog.Items, i => i.Title == "Some Movie (2019)");
        Assert.Contains(catalog.Items, i => i.Title == "Another Film (2020)");
        Assert.DoesNotContain(catalog.Items, i => i.Title.Contains("notes"));
    }

    [Fact]
    public async Task A_query_filters_by_title()
    {
        var catalog = await Movies().SearchAsync("movies", "another", Ctx(), default);
        var item = Assert.Single(catalog.Items);
        Assert.Equal("Another Film (2020)", item.Title);
    }

    [Fact]
    public async Task Resolve_returns_a_proxy_url_for_a_real_item()
    {
        var item = (await Movies().SearchAsync("movies", "some", Ctx(), default)).Items.Single();
        var stream = await Movies().ResolveAsync(item.Id, 0, Ctx(), default);
        Assert.NotNull(stream);
        Assert.StartsWith("proxy/locallib/", stream!.Url);
    }

    [Fact]
    public async Task An_id_whose_path_escapes_the_roots_resolves_to_nothing()
    {
        // An id for a real file OUTSIDE the configured roots must not resolve.
        var outside = Path.Combine(Path.GetTempPath(), "ebs-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);       // public static, encodes a path to an id
            var src = new LocalLibrarySource([_root], [], null, NullLogger<LocalLibrarySource>.Instance);
            Assert.Null(await src.ResolveAsync(evilId, 0, Ctx(), default));
        }
        finally { File.Delete(outside); }
    }

    private async Task<string> FirstItemIdAsync()
        => (await Movies().SearchAsync("movies", "some", Ctx(), default)).Items.Single().Id;

    [Fact]
    public async Task Open_without_a_range_serves_the_whole_file()
    {
        await using var r = await Movies().OpenAsync(await FirstItemIdAsync(), null, default);
        Assert.NotNull(r);
        Assert.Equal(200, r!.StatusCode);
        Assert.Equal(4, r.ContentLength);
        Assert.Equal("bytes", r.AcceptRanges);
        using var sink = new MemoryStream();
        await r.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.ToArray());
    }

    [Fact]
    public async Task Open_with_a_range_serves_206_and_the_slice()
    {
        await using var r = await Movies().OpenAsync(await FirstItemIdAsync(), "bytes=1-2", default);
        Assert.NotNull(r);
        Assert.Equal(206, r!.StatusCode);
        Assert.Equal(2, r.ContentLength);
        Assert.Equal("bytes 1-2/4", r.ContentRange);
        Assert.Equal("bytes", r.AcceptRanges);
        using var sink = new MemoryStream();
        await r.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 2, 3 }, sink.ToArray());
    }

    [Fact]
    public async Task Open_with_an_unsatisfiable_range_is_416()
    {
        await using var r = await Movies().OpenAsync(await FirstItemIdAsync(), "bytes=100-200", default);
        Assert.NotNull(r);
        Assert.Equal(416, r!.StatusCode);
        Assert.Equal("bytes */4", r.ContentRange);
    }

    [Fact]
    public async Task Open_on_an_out_of_roots_id_returns_null()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Null(await new LocalLibrarySource([_root], [], null, NullLogger<LocalLibrarySource>.Instance).OpenAsync(evilId, null, default));
        }
        finally { File.Delete(outside); }
    }

    private (string seriesRoot, string showDir) MakeShow()
    {
        var seriesRoot = Path.Combine(_root, "TV");
        var showDir = Path.Combine(seriesRoot, "Breaking Show");
        var seasonDir = Path.Combine(showDir, "Season 01");
        Directory.CreateDirectory(seasonDir);
        File.WriteAllBytes(Path.Combine(seasonDir, "Breaking.Show.S01E02.mkv"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(seasonDir, "Breaking.Show.S01E01.mkv"), new byte[] { 5, 6, 7, 8 });
        return (seriesRoot, showDir);
    }

    private LocalLibrarySource Series(string seriesRoot)
        => new([], [seriesRoot], null, NullLogger<LocalLibrarySource>.Instance);

    [Fact]
    public void No_series_roots_declares_no_series_catalog()
        => Assert.DoesNotContain(new LocalLibrarySource([_root], [], null, NullLogger<LocalLibrarySource>.Instance).Catalogs,
                                 c => c.Id == "series");

    [Fact]
    public void A_series_root_declares_the_series_catalog()
    {
        var (seriesRoot, _) = MakeShow();
        Assert.Contains(Series(seriesRoot).Catalogs, c => c.Id == "series" && c.MediaType == "series");
    }

    [Fact]
    public async Task Series_catalog_lists_each_show_as_an_expandable_item()
    {
        var (seriesRoot, _) = MakeShow();
        var catalog = await Series(seriesRoot).SearchAsync("series", null, Ctx(), default);
        var item = Assert.Single(catalog.Items);
        Assert.Equal("Breaking Show", item.Title);
        Assert.Equal("series", item.MediaType);
        Assert.True(item.Expandable);
    }

    [Fact]
    public async Task Series_query_filters_by_show_title()
    {
        var (seriesRoot, _) = MakeShow();
        Assert.Empty((await Series(seriesRoot).SearchAsync("series", "nomatch", Ctx(), default)).Items);
        Assert.Single((await Series(seriesRoot).SearchAsync("series", "breaking", Ctx(), default)).Items);
    }

    private async Task<string> ShowIdAsync(string seriesRoot)
        => (await Series(seriesRoot).SearchAsync("series", null, Ctx(), default)).Items.Single().Id;

    [Fact]
    public async Task Expanding_a_show_returns_its_episodes_ordered()
    {
        var (seriesRoot, _) = MakeShow();
        var episodes = await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default);
        Assert.Equal(2, episodes.Items.Count);
        Assert.Equal("S01E01", episodes.Items[0].Title);
        Assert.Equal("S01E02", episodes.Items[1].Title);
        Assert.All(episodes.Items, e => Assert.Equal("series", e.MediaType));
        Assert.All(episodes.Items, e => Assert.False(e.Expandable));
        Assert.Contains("Breaking.Show.S01E01.mkv", episodes.Items[0].Subtitle);
    }

    [Fact]
    public async Task Non_episode_files_under_a_show_are_excluded()
    {
        var (seriesRoot, showDir) = MakeShow();
        File.WriteAllBytes(Path.Combine(showDir, "trailer.mkv"), new byte[] { 0 }); // no SxxEyy → not an episode
        var episodes = await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default);
        Assert.Equal(2, episodes.Items.Count);
    }

    [Fact]
    public async Task A_file_id_does_not_expand()
    {
        var (seriesRoot, _) = MakeShow();
        var episodeId = (await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default)).Items[0].Id;
        Assert.Empty((await Series(seriesRoot).DetailAsync(episodeId, Ctx(), default)).Items); // a file id → nothing to expand
    }

    [Fact]
    public async Task A_series_root_id_does_not_expand()
    {
        var (seriesRoot, _) = MakeShow();
        // The series ROOT itself is never a show — a show is always a strict subfolder of a root.
        // An id forged for the root must not flatten the whole root into a giant episode list.
        var rootId = SafeLocalFileServer.EncodeId(seriesRoot);
        Assert.Empty((await Series(seriesRoot).DetailAsync(rootId, Ctx(), default)).Items);
    }

    [Fact]
    public async Task A_series_folder_id_is_not_served()
    {
        var (seriesRoot, _) = MakeShow();
        var showId = await ShowIdAsync(seriesRoot);
        Assert.Null(await Series(seriesRoot).ResolveAsync(showId, 0, Ctx(), default)); // a folder is never served
        Assert.Null(await Series(seriesRoot).OpenAsync(showId, null, default));
    }

    [Fact]
    public async Task An_episode_serves_with_range()
    {
        var (seriesRoot, _) = MakeShow();
        var episodeId = (await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default)).Items[0].Id;
        await using var r = await Series(seriesRoot).OpenAsync(episodeId, "bytes=1-2", default);
        Assert.NotNull(r);
        Assert.Equal(206, r!.StatusCode);
        Assert.Equal("bytes 1-2/4", r.ContentRange);
    }

    [Fact]
    public async Task Movie_row_uses_the_nfo_title_and_a_poster_thumbnail()
    {
        var mkv = Path.Combine(_root, "generic.mkv");
        File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real Title</title><year>2011</year><plot>P.</plot></movie>");
        File.WriteAllBytes(Path.Combine(_root, "generic-poster.jpg"), [9]);

        var item = Assert.Single((await Movies().SearchAsync("movies", "real", Ctx(), default)).Items);
        Assert.Equal("Real Title (2011)", item.Title);
        Assert.StartsWith("proxy/locallib/", item.ThumbnailUrl);
    }

    [Fact]
    public async Task MetaAsync_returns_overview_poster_and_year_for_a_movie()
    {
        var mkv = Path.Combine(_root, "generic.mkv");
        File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real Title</title><year>2011</year><plot>The plot.</plot></movie>");
        File.WriteAllBytes(Path.Combine(_root, "generic-poster.jpg"), [9]);

        var id = SafeLocalFileServer.EncodeId(mkv);
        var detail = await Movies().MetaAsync(id, Ctx(), default);
        Assert.NotNull(detail);
        Assert.Equal("Real Title", detail!.Title);
        Assert.Equal("The plot.", detail.Overview);
        Assert.StartsWith("proxy/locallib/", detail.ImageUrl);
        Assert.Contains(detail.Facts!, f => f.Label == "Year" && f.Value == "2011");
    }

    [Fact]
    public async Task MetaAsync_on_a_series_folder_reads_tvshow_nfo()
    {
        var (seriesRoot, showDir) = MakeShow();
        File.WriteAllText(Path.Combine(showDir, "tvshow.nfo"), "<tvshow><title>Breaking Show</title><plot>Show plot.</plot></tvshow>");
        var showId = (await Series(seriesRoot).SearchAsync("series", null, Ctx(), default)).Items.Single().Id;
        var detail = await Series(seriesRoot).MetaAsync(showId, Ctx(), default);
        Assert.Equal("Show plot.", detail!.Overview);
    }

    [Fact]
    public async Task MetaAsync_on_an_out_of_roots_id_is_null()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-out-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllBytes(outside, [1]);
        try { Assert.Null(await Movies().MetaAsync(SafeLocalFileServer.EncodeId(outside), Ctx(), default)); }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Episode_uses_the_episode_nfo_title()
    {
        var (seriesRoot, showDir) = MakeShow();
        var seasonDir = Path.Combine(showDir, "Season 01");
        File.WriteAllText(Path.Combine(seasonDir, "Breaking.Show.S01E01.nfo"), "<episodedetails><title>Pilot</title></episodedetails>");
        var showId = (await Series(seriesRoot).SearchAsync("series", null, Ctx(), default)).Items.Single().Id;
        var eps = await Series(seriesRoot).DetailAsync(showId, Ctx(), default);
        Assert.Equal("S01E01 - Pilot", eps.Items[0].Title);
    }

    [Fact]
    public async Task A_poster_id_serves_as_an_image()
    {
        var mkv = Path.Combine(_root, "img.mkv"); File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        var poster = Path.Combine(_root, "img-poster.png"); File.WriteAllBytes(poster, [7, 7, 7]);
        var id = SafeLocalFileServer.EncodeId(poster);
        await using var r = await Movies().OpenAsync(id, null, default);
        Assert.NotNull(r);
        Assert.Equal(200, r!.StatusCode);
        Assert.Equal("image/png", r.ContentType);
    }

    [Fact]
    public async Task Episode_poster_survives_a_list_browse_then_meta_panel_on_a_shared_cache()
    {
        // Order-dependence regression: opening the episode LIST (DetailAsync) populates a SHARED
        // cache entry keyed on the episode file + its sidecar; opening that episode's meta panel
        // (MetaAsync) then reads the SAME entry. Both must be built on the same cache instance, and
        // the poster the finder locates must survive the list browse — Inc 3 returned it.
        var (seriesRoot, showDir) = MakeShow();
        var seasonDir = Path.Combine(showDir, "Season 01");
        // A poster the finder locates for the episode file (folder poster next to the episode).
        File.WriteAllBytes(Path.Combine(seasonDir, "poster.jpg"), [9]);

        var cache = new SpyCache();
        var src = new LocalLibrarySource([], [seriesRoot], cache, NullLogger<LocalLibrarySource>.Instance);

        // 1) Browse the episode list FIRST — this fills the shared cache entry.
        var eps = await src.DetailAsync(SafeLocalFileServer.EncodeId(showDir), Ctx(), default);
        var episodeId = eps.Items[0].Id;

        // 2) Now open the episode's meta panel — it reads the shared entry and must keep the poster.
        var detail = await src.MetaAsync(episodeId, Ctx(), default);
        Assert.NotNull(detail);
        Assert.NotNull(detail!.ImageUrl); // was null before the fix: the list browse cached a null poster
        Assert.StartsWith("proxy/locallib/", detail.ImageUrl);
    }

    [Fact]
    public async Task A_cached_source_returns_the_same_movies_as_an_uncached_one()
    {
        var mkv = Path.Combine(_root, "generic.mkv"); File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real</title><year>2011</year></movie>");
        File.WriteAllBytes(Path.Combine(_root, "generic-poster.jpg"), [9]);

        // The shared fixture also seeds two other movies, so filter to the one under test.
        var uncached = (await Movies().SearchAsync("movies", "real", Ctx(), default)).Items.Single();
        var cached = (await CachedMovies(new SpyCache()).SearchAsync("movies", "real", Ctx(), default)).Items.Single();

        Assert.Equal(uncached.Title, cached.Title);
        Assert.Equal(uncached.ThumbnailUrl, cached.ThumbnailUrl);
    }

    [Fact]
    public async Task A_second_browse_of_an_unchanged_file_does_not_re_store()
    {
        var mkv = Path.Combine(_root, "generic.mkv"); File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real</title></movie>");
        var spy = new SpyCache();
        var src = CachedMovies(spy);

        await src.SearchAsync("movies", null, Ctx(), default);
        var setsAfterFirst = spy.Sets;
        await src.SearchAsync("movies", null, Ctx(), default);

        Assert.Equal(setsAfterFirst, spy.Sets); // second browse was a pure hit — no new stores
        Assert.True(spy.Gets >= 2);             // and it did consult the cache
    }
}
