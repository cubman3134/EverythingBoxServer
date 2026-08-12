using EverythingBox.Server.Abstractions;
using EverythingBox.Server.MusicLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public sealed class MusicLibrarySourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "eblib-music-" + Guid.NewGuid().ToString("N"));
    private readonly string _coverCache =
        Path.Combine(Path.GetTempPath(), "eblib-music-covers-" + Guid.NewGuid().ToString("N"));

    public MusicLibrarySourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_coverCache);
        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private MusicLibrarySource Source(params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, _coverCache, null, NullLogger<MusicLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    private static string IdOf(string proxyUrl) => proxyUrl.Split('/')[2];

    // ---- runtime-synthesized tagged-audio fixture (no committed binary) ----

    // A minimal wholly-synthetic MPEG-1 Layer III stream: a silent 128kbps/44.1kHz frame repeated. ATL
    // detects it as an MP3 and writes a real ID3v2 tag over it on Save(), giving deterministic tagged
    // audio at runtime — SOURCE (a byte template), not a committed fixture.
    private static byte[] SilentMp3()
    {
        byte[] header = [0xFF, 0xFB, 0x90, 0x00]; // sync + MPEG1 L3 128kbps 44.1kHz stereo
        const int frameLen = 417;                 // 144 * 128000 / 44100
        const int frames = 24;
        var buf = new byte[frameLen * frames];
        for (var i = 0; i < frames; i++)
            Array.Copy(header, 0, buf, i * frameLen, header.Length);
        return buf;
    }

    private static string WriteTaggedTrack(
        string dir, string fileName,
        string? artist = null, string? albumArtist = null, string? album = null, string? title = null,
        int trackNo = 0, int discNo = 0, int year = 0)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, SilentMp3());

        var t = new ATL.Track(path);
        if (artist is not null) t.Artist = artist;
        if (albumArtist is not null) t.AlbumArtist = albumArtist;
        if (album is not null) t.Album = album;
        if (title is not null) t.Title = title;
        if (trackNo > 0) t.TrackNumber = trackNo;
        if (discNo > 0) t.DiscNumber = discNo;
        if (year > 0) t.Year = year;
        Assert.True(t.Save(), $"ATL failed to write tags to {fileName}");
        return path;
    }

    // Alice/Debut (2001) with a sibling cover.jpg, and a Various-Artists compilation whose two tracks
    // are performed by Bob and Carol — the classic case that must collapse to ONE artist/ONE album.
    private void MakeLibrary()
    {
        var aliceDir = Path.Combine(_root, "Alice - Debut");
        WriteTaggedTrack(aliceDir, "01 - Intro.mp3",
            artist: "Alice", album: "Debut", title: "Intro", trackNo: 1, year: 2001);
        File.WriteAllBytes(Path.Combine(aliceDir, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);

        var compDir = Path.Combine(_root, "VA - Comp");
        WriteTaggedTrack(compDir, "01.mp3",
            artist: "Bob", albumArtist: "Various Artists", album: "Comp", title: "Track One", trackNo: 1);
        WriteTaggedTrack(compDir, "02.mp3",
            artist: "Carol", albumArtist: "Various Artists", album: "Comp", title: "Track Two", trackNo: 2);
    }

    private async Task<CatalogItem> ArtistItem(string name)
    {
        var catalog = await Source().SearchAsync("music", null, Ctx(), default);
        return Assert.Single(catalog.Items, i => i.Title == name);
    }

    // ---- catalog surface ----

    [Fact]
    public void No_roots_configured_has_no_catalogs()
        => Assert.Empty(new MusicLibrarySource([], _coverCache, null, NullLogger<MusicLibrarySource>.Instance).Catalogs);

    [Fact]
    public void A_configured_root_advertises_the_music_catalog()
    {
        var c = Assert.Single(Source().Catalogs);
        Assert.Equal("music", c.Id);
        Assert.Equal("music", c.Kind);
    }

    [Fact]
    public async Task A_non_music_catalog_is_empty()
    {
        MakeLibrary();
        Assert.Empty((await Source().SearchAsync("nope", null, Ctx(), default)).Items);
    }

    // ---- artists → albums → tracks ----

    [Fact]
    public async Task Search_lists_the_artists_including_the_compilation_album_artist()
    {
        MakeLibrary();

        var catalog = await Source().SearchAsync("music", null, Ctx(), default);

        Assert.All(catalog.Items, i => Assert.Equal("music", i.Kind));
        Assert.All(catalog.Items, i => Assert.True(i.Expandable));
        Assert.Contains(catalog.Items, i => i.Title == "Alice");
        Assert.Contains(catalog.Items, i => i.Title == "Various Artists"); // NOT Bob/Carol
        Assert.DoesNotContain(catalog.Items, i => i.Title == "Bob" || i.Title == "Carol");
    }

    [Fact]
    public async Task Search_filters_by_query_on_the_artist_name()
    {
        MakeLibrary();

        var catalog = await Source().SearchAsync("music", "various", Ctx(), default);

        var only = Assert.Single(catalog.Items);
        Assert.Equal("Various Artists", only.Title);
    }

    [Fact]
    public async Task An_artist_expands_into_its_albums()
    {
        MakeLibrary();
        var artist = await ArtistItem("Various Artists");

        var albums = await Source().DetailAsync(artist.Id, Ctx(), default);

        var album = Assert.Single(albums.Items);
        Assert.Equal("Comp", album.Title);
        Assert.Equal("Various Artists", album.Subtitle);
        Assert.True(album.Expandable);
        Assert.Equal("music", album.Kind);
    }

    [Fact]
    public async Task An_album_title_carries_its_year()
    {
        MakeLibrary();
        var artist = await ArtistItem("Alice");

        var album = Assert.Single((await Source().DetailAsync(artist.Id, Ctx(), default)).Items);
        Assert.Equal("Debut (2001)", album.Title);
    }

    [Fact]
    public async Task An_album_expands_into_its_tracks_sorted_and_numbered()
    {
        MakeLibrary();
        var artist = await ArtistItem("Various Artists");
        var album = Assert.Single((await Source().DetailAsync(artist.Id, Ctx(), default)).Items);

        var tracks = await Source().DetailAsync(album.Id, Ctx(), default);

        Assert.Equal(2, tracks.Items.Count);
        Assert.All(tracks.Items, i => Assert.False(i.Expandable));
        Assert.All(tracks.Items, i => Assert.Equal("music", i.Kind));
        Assert.Equal(["01. Track One", "02. Track Two"], tracks.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Detail_on_a_foreign_id_lists_nothing()
    {
        MakeLibrary();
        var outside = Path.Combine(Path.GetTempPath(), "eblib-music-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Empty((await Source().DetailAsync(evilId, Ctx(), default)).Items);
        }
        finally { Directory.Delete(outside, true); }
    }

    // ---- resolve + serve ----

    private async Task<CatalogItem> AnyCompTrack()
    {
        var artist = await ArtistItem("Various Artists");
        var album = Assert.Single((await Source().DetailAsync(artist.Id, Ctx(), default)).Items);
        return (await Source().DetailAsync(album.Id, Ctx(), default)).Items[0];
    }

    [Fact]
    public async Task Resolve_on_a_track_id_returns_a_proxy_stream_with_audio_mime()
    {
        MakeLibrary();
        var track = await AnyCompTrack();

        var stream = await Source().ResolveAsync(track.Id, 0, Ctx(), default);

        Assert.NotNull(stream);
        Assert.StartsWith("proxy/musiclib/", stream!.Url);
        Assert.Equal("audio/mpeg", stream.Mime);
    }

    [Fact]
    public async Task Resolve_on_a_foreign_id_is_null()
    {
        MakeLibrary();
        var outside = Path.Combine(Path.GetTempPath(), "eblib-music-outside-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(outside, [9]);
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Null(await Source().ResolveAsync(evilId, 0, Ctx(), default));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Open_serves_a_track_with_range()
    {
        MakeLibrary();
        var track = await AnyCompTrack();
        var expectedFirstTen = File.ReadAllBytes(SafeLocalFileServer.TryDecodeId(track.Id)!)[..10];

        await using var partial = await Source().OpenAsync(track.Id, "bytes=0-9", default);
        Assert.NotNull(partial);
        Assert.Equal(206, partial!.StatusCode);
        using var sink = new MemoryStream();
        await partial.Body.CopyToAsync(sink);
        var got = sink.ToArray();
        Assert.Equal(10, got.Length);
        Assert.Equal(expectedFirstTen, got);
    }

    [Fact]
    public async Task Open_serves_an_album_cover_with_200()
    {
        MakeLibrary();
        var artist = await ArtistItem("Alice");
        var album = Assert.Single((await Source().DetailAsync(artist.Id, Ctx(), default)).Items);
        Assert.NotNull(album.ThumbnailUrl);
        Assert.StartsWith("proxy/musiclib/", album.ThumbnailUrl);

        var coverId = IdOf(album.ThumbnailUrl!);
        await using var resp = await Source().OpenAsync(coverId, null, default);
        Assert.NotNull(resp);
        Assert.Equal(200, resp!.StatusCode);
        Assert.StartsWith("image/", resp.ContentType);
        using var sink = new MemoryStream();
        await resp.Body.CopyToAsync(sink);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], sink.ToArray());
    }

    [Fact]
    public async Task Open_on_a_traversal_id_serves_nothing()
    {
        MakeLibrary();
        var outside = Path.Combine(Path.GetTempPath(), "eblib-music-outside-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(outside, [9]);
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Null(await Source().OpenAsync(evilId, null, default));
        }
        finally { File.Delete(outside); }
    }
}
