using EverythingBox.Server.Abstractions;
using EverythingBox.Server.MusicLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Covers the <see cref="IMusicLibrary"/> projection <see cref="MusicLibrarySource"/> exposes on top of
/// its (unchanged) IMediaSource browse: the DTO mapping off the scanned index, AlbumList ordering,
/// search, cover-art resolution, ranged track serving, and the local star/scrobble/playlist store —
/// including that a star SURVIVES a fresh source over the same state dir (persisted, not in-memory).
/// The tagged-audio tree is synthesized at runtime (no committed binary), reusing the same SilentMp3
/// template MusicScannerTests uses.
/// </summary>
public sealed class MusicLibraryImplTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ebmusicimpl-" + Guid.NewGuid().ToString("N"));
    private readonly string _coverCache =
        Path.Combine(Path.GetTempPath(), "ebmusicimpl-covers-" + Guid.NewGuid().ToString("N"));
    private readonly string _state =
        Path.Combine(Path.GetTempPath(), "ebmusicimpl-state-" + Guid.NewGuid().ToString("N"));

    public MusicLibraryImplTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_coverCache);
        TryDelete(_state);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private IMusicLibrary Library()
        => new MusicLibrarySource([_root], _coverCache, null, NullLogger<MusicLibrarySource>.Instance, _state);

    // ---- runtime-synthesized tagged-audio fixture (no committed binary) ----

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
        int trackNo = 0, int discNo = 0, int year = 0, string? genre = null)
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
        if (genre is not null) t.Genre = genre;
        Assert.True(t.Save(), $"ATL failed to write tags to {fileName}");
        return path;
    }

    // Alice/Debut (2001, Rock) with a sibling cover.jpg + one track; Zed/Later (2010, Jazz) with one
    // track. The two album names sort A-before-Z, giving AlbumList("alphabeticalByName") a deterministic
    // order to assert; the two distinct genres give byGenre/RandomSongs/Genres something to filter on.
    private void MakeLibrary()
    {
        var aliceDir = Path.Combine(_root, "Alice - Debut");
        WriteTaggedTrack(aliceDir, "01 - Intro.mp3",
            artist: "Alice", album: "Debut", title: "Intro", trackNo: 1, year: 2001, genre: "Rock");
        File.WriteAllBytes(Path.Combine(aliceDir, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);

        var zedDir = Path.Combine(_root, "Zed - Later");
        WriteTaggedTrack(zedDir, "01 - Outro.mp3",
            artist: "Zed", album: "Later", title: "Outro", trackNo: 1, year: 2010, genre: "Jazz");
    }

    private SongInfo AliceSong()
    {
        var lib = Library();
        var artist = Assert.Single(lib.Artists(), a => a.Name == "Alice");
        var album = Assert.Single(lib.Artist(artist.Id)!.Value.Albums);
        return Assert.Single(lib.Album(album.Id)!.Value.Songs);
    }

    // ---- DTO mapping ----

    [Fact]
    public void Folders_is_a_single_music_folder()
    {
        var f = Assert.Single(Library().Folders());
        Assert.Equal("1", f.Id);
        Assert.Equal("Music", f.Name);
    }

    [Fact]
    public void Artists_and_Artist_return_the_scanned_library()
    {
        MakeLibrary();
        var lib = Library();

        var names = lib.Artists().Select(a => a.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["Alice", "Zed"], names);

        var alice = Assert.Single(lib.Artists(), a => a.Name == "Alice");
        Assert.Equal(1, alice.AlbumCount);
        Assert.NotNull(alice.CoverArtId);   // first album's sibling cover

        var albums = lib.Artist(alice.Id)!.Value.Albums;
        var debut = Assert.Single(albums);
        Assert.Equal("Debut", debut.Name);
        Assert.Equal(2001, debut.Year);
        Assert.Equal(1, debut.SongCount);
    }

    [Fact]
    public void Album_and_Song_carry_the_right_fields()
    {
        MakeLibrary();
        var lib = Library();
        var song = AliceSong();

        Assert.Equal("Intro", song.Title);
        Assert.Equal("Debut", song.Album);
        Assert.Equal("Alice", song.Artist);
        Assert.Equal(1, song.Track);
        Assert.Equal("mp3", song.Suffix);           // extension WITHOUT the dot
        Assert.Equal("audio/mpeg", song.ContentType);
        Assert.NotNull(song.SizeBytes);
        Assert.True(song.SizeBytes > 0);
        Assert.NotNull(song.CoverArtId);            // album cover id
        Assert.False(song.Starred);

        // Song(id) round-trips to the same track.
        Assert.Equal(song.Id, lib.Song(song.Id)!.Id);
    }

    // ---- AlbumList ----

    [Fact]
    public void AlbumList_alphabeticalByName_orders_by_name()
    {
        MakeLibrary();
        var albums = Library().AlbumList("alphabeticalByName", size: 50, offset: 0, genre: null, fromYear: null, toYear: null);
        Assert.Equal(["Debut", "Later"], albums.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void AlbumList_newest_orders_by_year_descending()
    {
        MakeLibrary();
        var albums = Library().AlbumList("newest", 50, 0, null, null, null);
        Assert.Equal(["Later", "Debut"], albums.Select(a => a.Name).ToArray());   // 2010 before 2001
    }

    [Fact]
    public void AlbumList_unknown_type_is_empty()
    {
        MakeLibrary();
        Assert.Empty(Library().AlbumList("no-such-type", 50, 0, null, null, null));
    }

    // ---- Genre ----

    [Fact]
    public void Album_and_Song_carry_the_track_genre()
    {
        MakeLibrary();
        var lib = Library();

        var song = AliceSong();
        Assert.Equal("Rock", song.Genre);

        var alice = Assert.Single(lib.Artists(), a => a.Name == "Alice");
        var debut = Assert.Single(lib.Artist(alice.Id)!.Value.Albums);
        Assert.Equal("Rock", debut.Genre);
    }

    [Fact]
    public void AlbumList_byGenre_returns_only_matching_albums()
    {
        MakeLibrary();
        // Case-insensitive match; only the Rock album (Debut) comes back, not the Jazz one (Later).
        var rock = Library().AlbumList("byGenre", 50, 0, genre: "rock", fromYear: null, toYear: null);
        var album = Assert.Single(rock);
        Assert.Equal("Debut", album.Name);
        Assert.Equal("Rock", album.Genre);

        // A byGenre with no genre matches nothing.
        Assert.Empty(Library().AlbumList("byGenre", 50, 0, genre: null, fromYear: null, toYear: null));
    }

    [Fact]
    public void RandomSongs_byGenre_returns_only_matching_songs()
    {
        MakeLibrary();
        var jazz = Library().RandomSongs(size: 50, genre: "Jazz");
        var song = Assert.Single(jazz);
        Assert.Equal("Outro", song.Title);
        Assert.Equal("Jazz", song.Genre);
    }

    [Fact]
    public void Genres_lists_the_distinct_genres_with_counts()
    {
        MakeLibrary();
        var genres = Library().Genres();

        // Sorted by name: Jazz before Rock, one song + one album each.
        Assert.Equal(["Jazz", "Rock"], genres.Select(g => g.Name).ToArray());
        Assert.All(genres, g => Assert.Equal(1, g.SongCount));
        Assert.All(genres, g => Assert.Equal(1, g.AlbumCount));
    }

    // ---- Search ----

    [Fact]
    public void Search_finds_by_name()
    {
        MakeLibrary();
        var result = Library().Search("debut", artistCount: 10, albumCount: 10, songCount: 10);
        var album = Assert.Single(result.Albums);
        Assert.Equal("Debut", album.Name);

        var byArtist = Library().Search("zed", 10, 10, 10);
        Assert.Contains(byArtist.Artists, a => a.Name == "Zed");
    }

    // ---- CoverArt ----

    [Fact]
    public void CoverArt_resolves_the_sibling_cover_with_an_image_mime()
    {
        MakeLibrary();
        var song = AliceSong();
        Assert.NotNull(song.CoverArtId);

        var cover = Library().CoverArt(song.CoverArtId!);
        Assert.NotNull(cover);
        Assert.EndsWith("cover.jpg", cover!.Value.Path);
        Assert.StartsWith("image/", cover.Value.ContentType);
    }

    // ---- OpenTrackAsync ----

    [Fact]
    public async Task OpenTrackAsync_serves_a_ranged_206()
    {
        MakeLibrary();
        var song = AliceSong();

        await using var partial = await Library().OpenTrackAsync(song.Id, "bytes=0-9", default);
        Assert.NotNull(partial);
        Assert.Equal(206, partial!.StatusCode);
        using var sink = new MemoryStream();
        await partial.Body.CopyToAsync(sink);
        Assert.Equal(10, sink.ToArray().Length);
    }

    [Fact]
    public async Task OpenTrackAsync_on_a_non_track_id_is_null()
    {
        MakeLibrary();
        // A well-formed id for a real file that is NOT a scanned track serves nothing.
        var outside = Path.Combine(Path.GetTempPath(), "ebmusicimpl-outside-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(outside, [9]);
        try
        {
            Assert.Null(await Library().OpenTrackAsync(SafeLocalFileServer.EncodeId(outside), null, default));
        }
        finally { File.Delete(outside); }
    }

    // ---- local state store ----

    [Fact]
    public void SetStarred_is_reflected_and_survives_a_reload()
    {
        MakeLibrary();
        var song = AliceSong();

        // Star it on one source instance...
        Library().SetStarred(song.Id, true);

        // ...and a FRESH source over the same state dir sees it — proving it persisted, not in-memory.
        var reloaded = Library().Song(song.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Starred);
    }

    [Fact]
    public void Scrobble_records_into_the_store()
    {
        MakeLibrary();
        var song = AliceSong();
        var when = DateTimeOffset.UtcNow;

        // Reuse the same store the source persisted into to read the history back.
        var lib = Library();
        lib.Scrobble(song.Id, when);

        var store = new MusicStateStore(_state);
        var row = Assert.Single(store.Scrobbles());
        Assert.Equal(song.Id, row.SongId);
    }

    [Fact]
    public void A_playlist_round_trips_through_the_store()
    {
        MakeLibrary();
        var song = AliceSong();

        // Playlists are created through the store (there is no create verb on IMusicLibrary v1).
        var writer = new MusicStateStore(_state);
        writer.SavePlaylist("pl-1", "Favourites", [song.Id]);

        // A fresh source over the same state dir projects it, resolving the song id to a full SongInfo.
        var lib = Library();
        var pl = lib.Playlist("pl-1");
        Assert.NotNull(pl);
        Assert.Equal("Favourites", pl!.Name);
        Assert.Equal(1, pl.SongCount);
        Assert.Equal(song.Id, Assert.Single(pl.Songs).Id);

        Assert.Contains(lib.Playlists(), p => p.Id == "pl-1");
    }
}
