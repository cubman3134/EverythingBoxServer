using EverythingBox.Server.Abstractions;
using EverythingBox.Server.LocalLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class MovieLibrarySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-lib-" + Guid.NewGuid().ToString("N"));

    public MovieLibrarySourceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "Some.Movie.2019.1080p.BluRay.mkv"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_root, "Another Film (2020).mp4"), new byte[] { 5, 6, 7, 8 });
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a movie");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } GC.SuppressFinalize(this); }

    private MovieLibrarySource Source(params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, NullLogger<MovieLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    [Fact]
    public void No_configured_roots_declares_no_catalog()
        => Assert.Empty(new MovieLibrarySource([], NullLogger<MovieLibrarySource>.Instance).Catalogs);

    [Fact]
    public void A_configured_root_declares_the_movies_catalog()
    {
        var c = Assert.Single(Source().Catalogs);
        Assert.Equal("movies", c.Id);
        Assert.Equal("movie", c.MediaType);
    }

    [Fact]
    public async Task Scans_only_video_files_and_titles_them_from_the_filename()
    {
        var catalog = await Source().SearchAsync("movies", null, Ctx(), default);
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
        var catalog = await Source().SearchAsync("movies", "another", Ctx(), default);
        var item = Assert.Single(catalog.Items);
        Assert.Equal("Another Film (2020)", item.Title);
    }

    [Fact]
    public async Task Resolve_returns_a_proxy_url_for_a_real_item()
    {
        var item = (await Source().SearchAsync("movies", "some", Ctx(), default)).Items.Single();
        var stream = await Source().ResolveAsync(item.Id, 0, Ctx(), default);
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
            var evilId = MovieLibrarySource.EncodeId(outside);       // internal, visible to tests
            var src = new MovieLibrarySource([_root], NullLogger<MovieLibrarySource>.Instance);
            Assert.Null(await src.ResolveAsync(evilId, 0, Ctx(), default));
        }
        finally { File.Delete(outside); }
    }

    private async Task<string> FirstItemIdAsync()
        => (await Source().SearchAsync("movies", "some", Ctx(), default)).Items.Single().Id;

    [Fact]
    public async Task Open_without_a_range_serves_the_whole_file()
    {
        await using var r = await Source().OpenAsync(await FirstItemIdAsync(), null, default);
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
        await using var r = await Source().OpenAsync(await FirstItemIdAsync(), "bytes=1-2", default);
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
        await using var r = await Source().OpenAsync(await FirstItemIdAsync(), "bytes=100-200", default);
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
            var evilId = MovieLibrarySource.EncodeId(outside);
            Assert.Null(await new MovieLibrarySource([_root], NullLogger<MovieLibrarySource>.Instance).OpenAsync(evilId, null, default));
        }
        finally { File.Delete(outside); }
    }
}
