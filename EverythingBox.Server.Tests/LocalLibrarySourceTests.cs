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
        => new(roots.Length == 0 ? [_root] : roots, [], NullLogger<LocalLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    [Fact]
    public void No_configured_roots_declares_no_catalog()
        => Assert.Empty(new LocalLibrarySource([], [], NullLogger<LocalLibrarySource>.Instance).Catalogs);

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
            var evilId = LocalLibrarySource.EncodeId(outside);       // internal, visible to tests
            var src = new LocalLibrarySource([_root], [], NullLogger<LocalLibrarySource>.Instance);
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
            var evilId = LocalLibrarySource.EncodeId(outside);
            Assert.Null(await new LocalLibrarySource([_root], [], NullLogger<LocalLibrarySource>.Instance).OpenAsync(evilId, null, default));
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
        => new([], [seriesRoot], NullLogger<LocalLibrarySource>.Instance);

    [Fact]
    public void No_series_roots_declares_no_series_catalog()
        => Assert.DoesNotContain(new LocalLibrarySource([_root], [], NullLogger<LocalLibrarySource>.Instance).Catalogs,
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
}
