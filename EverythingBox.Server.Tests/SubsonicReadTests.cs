using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EverythingBox.Server.Tests;

/// <summary>Integration tests over the Subsonic read endpoints (getMusicFolders … getRandomSongs),
/// driving the real host booted with Subsonic enabled and the musiclib plugin loaded via
/// <see cref="SubsonicServerFactory"/> over its synthesized tagged library (Alice/Nova and a
/// "Various Artists" compilation, tagged Rock/Jazz/Pop). Every request is authenticated with a valid
/// t/s token (reusing the same MD5(token+salt) helper as the Task-1 auth tests). getArtist and getAlbum
/// are asserted against BOTH the XML default and f=json, proving the one node model / two renderers.</summary>
[Collection(SubsonicServerCollection.Name)]
public class SubsonicReadTests
{
    private readonly SubsonicServerFactory _factory;

    public SubsonicReadTests(SubsonicServerFactory factory) => _factory = factory;

    private static string Md5Hex(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Auth(string salt) => $"u=admin&t={Md5Hex(SubsonicServerFactory.Token + salt)}&s={salt}";

    // Salts are unique per call so nothing collides across the suite's requests.
    private async Task<XElement> XmlAsync(string endpointAndQuery)
    {
        var url = $"/rest/{endpointAndQuery}&{Auth(Guid.NewGuid().ToString("N")[..8])}";
        var resp = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        return XElement.Parse(body);   // the <subsonic-response> root (no namespace)
    }

    private async Task<JsonElement> JsonAsync(string endpointAndQuery)
    {
        var url = $"/rest/{endpointAndQuery}&f=json&{Auth(Guid.NewGuid().ToString("N")[..8])}";
        var resp = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("subsonic-response").Clone();
    }

    private static IEnumerable<XElement> Descend(XElement root, string name) => root.Descendants(name);
    private static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";

    // ---- getMusicFolders ----

    [Fact]
    public async Task GetMusicFolders_lists_the_single_music_folder()
    {
        var root = await XmlAsync("getMusicFolders?");
        var folders = Descend(root, "musicFolder").ToList();
        Assert.NotEmpty(folders);
        Assert.All(folders, f => Assert.NotEmpty(Attr(f, "id")));
        Assert.All(folders, f => Assert.NotEmpty(Attr(f, "name")));
    }

    // ---- getArtists / getIndexes ----

    [Fact]
    public async Task GetArtists_groups_under_first_letter_indexes_including_various_artists()
    {
        var root = await XmlAsync("getArtists?");
        var indexes = Descend(root, "index").ToList();

        // Indexes are first-letter groups; our library has A (Alice), N (Nova), V (Various Artists).
        var letters = indexes.Select(i => Attr(i, "name")).ToList();
        Assert.Contains("A", letters);
        Assert.Contains("N", letters);
        Assert.Contains("V", letters);

        var names = Descend(root, "artist").Select(a => Attr(a, "name")).ToList();
        Assert.Contains("Alice", names);
        Assert.Contains("Nova", names);
        Assert.Contains("Various Artists", names);   // the compilation collapsed to one artist

        // The "V" index holds Various Artists, and every artist carries an id + albumCount.
        var vIndex = indexes.Single(i => Attr(i, "name") == "V");
        var various = Descend(vIndex, "artist").Single(a => Attr(a, "name") == "Various Artists");
        Assert.NotEmpty(Attr(various, "id"));
        Assert.Equal("1", Attr(various, "albumCount"));
    }

    [Fact]
    public async Task GetIndexes_serves_the_same_grouping_under_an_indexes_root()
    {
        var root = await XmlAsync("getIndexes?");
        Assert.NotNull(root.Element("indexes"));
        Assert.Contains("Nova", Descend(root, "artist").Select(a => Attr(a, "name")));
    }

    // ---- getArtist ----

    [Fact]
    public async Task GetArtist_returns_the_artist_with_its_albums_in_xml()
    {
        var novaId = await ArtistIdOf("Nova");
        var root = await XmlAsync($"getArtist?id={novaId}");

        var artist = root.Element("artist")!;
        Assert.Equal("Nova", Attr(artist, "name"));

        var albums = Descend(artist, "album").Select(a => Attr(a, "name")).ToList();
        Assert.Contains("Nova Nights", albums);
        Assert.Contains("Nova Dawn", albums);
        Assert.Equal(2, albums.Count);
    }

    [Fact]
    public async Task GetArtist_returns_the_artist_with_its_albums_in_json()
    {
        var novaId = await ArtistIdOf("Nova");
        var resp = await JsonAsync($"getArtist?id={novaId}");

        var artist = resp.GetProperty("artist");
        Assert.Equal("Nova", artist.GetProperty("name").GetString());

        // Two albums collapse to a JSON array under "album" (the renderer's array-collapse).
        var albumNames = artist.GetProperty("album").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Contains("Nova Nights", albumNames);
        Assert.Contains("Nova Dawn", albumNames);
    }

    [Fact]
    public async Task GetArtist_with_an_unknown_id_fails_with_code_70()
    {
        var root = await XmlAsync("getArtist?id=ar-does-not-exist");
        AssertFailed70(root);
    }

    // ---- getAlbum ----

    [Fact]
    public async Task GetAlbum_returns_the_album_with_its_songs_in_xml()
    {
        var albumId = await AlbumIdOf("Nova", "Nova Nights");
        var root = await XmlAsync($"getAlbum?id={albumId}");

        var album = root.Element("album")!;
        Assert.Equal("Nova Nights", Attr(album, "name"));
        Assert.Equal("2", Attr(album, "songCount"));

        var songs = Descend(album, "song").ToList();
        Assert.Equal(2, songs.Count);
        Assert.Contains(songs, s => Attr(s, "title") == "Nova Theme");
        Assert.All(songs, s =>
        {
            Assert.Equal("mp3", Attr(s, "suffix"));
            Assert.Equal("audio/mpeg", Attr(s, "contentType"));
            Assert.NotNull(s.Attribute("duration"));            // duration emitted (present)
            Assert.Equal("false", Attr(s, "isDir"));
            Assert.Equal("music", Attr(s, "type"));
            Assert.Equal(albumId, Attr(s, "parent"));           // parent = albumId
        });
    }

    [Fact]
    public async Task GetAlbum_returns_the_album_with_its_songs_in_json()
    {
        var albumId = await AlbumIdOf("Nova", "Nova Nights");
        var resp = await JsonAsync($"getAlbum?id={albumId}");

        var album = resp.GetProperty("album");
        Assert.Equal("Nova Nights", album.GetProperty("name").GetString());

        // Typed JSON: songCount is a native number (not "2"), while id/name stay strings.
        var songCount = album.GetProperty("songCount");
        Assert.Equal(JsonValueKind.Number, songCount.ValueKind);
        Assert.Equal(2, songCount.GetInt32());
        Assert.Equal(JsonValueKind.String, album.GetProperty("id").ValueKind);

        var songs = album.GetProperty("song").EnumerateArray().ToList();
        Assert.Equal(2, songs.Count);
        // isDir is a native boolean; duration/track are native numbers on each song.
        Assert.All(songs, s =>
        {
            Assert.Equal(JsonValueKind.False, s.GetProperty("isDir").ValueKind);
            Assert.Equal(JsonValueKind.Number, s.GetProperty("duration").ValueKind);
        });
        Assert.All(songs, s =>
        {
            Assert.Equal("mp3", s.GetProperty("suffix").GetString());
            Assert.Equal("audio/mpeg", s.GetProperty("contentType").GetString());
            Assert.Equal("music", s.GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task GetAlbum_with_an_unknown_id_fails_with_code_70()
    {
        var root = await XmlAsync("getAlbum?id=nope");
        AssertFailed70(root);

        // Same failure envelope in JSON.
        var resp = await JsonAsync("getAlbum?id=nope");
        Assert.Equal("failed", resp.GetProperty("status").GetString());
        Assert.Equal("70", resp.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- getSong ----

    [Fact]
    public async Task GetSong_returns_one_song()
    {
        var (albumId, _) = (await AlbumIdOf("Nova", "Nova Nights"), 0);
        var albumRoot = await XmlAsync($"getAlbum?id={albumId}");
        var songId = Attr(Descend(albumRoot, "song").First(), "id");
        Assert.NotEmpty(songId);

        var root = await XmlAsync($"getSong?id={songId}");
        var song = root.Element("song")!;
        Assert.Equal(songId, Attr(song, "id"));
        Assert.Equal("audio/mpeg", Attr(song, "contentType"));
    }

    [Fact]
    public async Task GetSong_with_an_unknown_id_fails_with_code_70()
        => AssertFailed70(await XmlAsync("getSong?id=nope"));

    // ---- getAlbumList2 ----

    [Fact]
    public async Task GetAlbumList2_alphabeticalByName_orders_albums()
    {
        var root = await XmlAsync("getAlbumList2?type=alphabeticalByName");
        var names = Descend(root, "album").Select(a => Attr(a, "name")).ToList();

        Assert.Equal(new[] { "Debut", "Nova Dawn", "Nova Nights", "Summer Hits" }, names);
    }

    [Fact]
    public async Task GetAlbumList2_byGenre_filters_to_the_requested_genre()
    {
        var root = await XmlAsync("getAlbumList2?type=byGenre&genre=Jazz");
        var names = Descend(root, "album").Select(a => Attr(a, "name")).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "Nova Dawn", "Nova Nights" }, names);   // only the Jazz albums
        Assert.DoesNotContain("Debut", names);
        Assert.DoesNotContain("Summer Hits", names);
    }

    // ---- getGenres ----

    [Fact]
    public async Task GetGenres_lists_the_genres_with_counts_and_name_as_text()
    {
        var root = await XmlAsync("getGenres?");
        var genres = Descend(root, "genre").ToList();

        // The genre name is the element TEXT, not an attribute (Subsonic quirk).
        var names = genres.Select(g => g.Value).ToList();
        Assert.Contains("Rock", names);
        Assert.Contains("Jazz", names);
        Assert.Contains("Pop", names);

        var jazz = genres.Single(g => g.Value == "Jazz");
        Assert.Equal("3", Attr(jazz, "songCount"));    // Nova Theme, Second Star, Dawn
        Assert.Equal("2", Attr(jazz, "albumCount"));   // Nova Nights, Nova Dawn
    }

    [Fact]
    public async Task GetGenres_carries_the_name_as_json_value()
    {
        var resp = await JsonAsync("getGenres?");
        var names = resp.GetProperty("genres").GetProperty("genre").EnumerateArray()
            .Select(g => g.GetProperty("value").GetString()).ToList();
        Assert.Contains("Jazz", names);
    }

    // ---- search3 ----

    [Fact]
    public async Task Search3_finds_the_artist_album_and_song_for_a_shared_token()
    {
        // "Nova" appears as an artist name, an album name, and a song title.
        var root = await XmlAsync("search3?query=Nova");
        var result = root.Element("searchResult3")!;

        Assert.Contains("Nova", Descend(result, "artist").Select(a => Attr(a, "name")));
        Assert.Contains("Nova Nights", Descend(result, "album").Select(a => Attr(a, "name")));
        Assert.Contains("Nova Theme", Descend(result, "song").Select(s => Attr(s, "title")));
    }

    // ---- getRandomSongs ----

    [Fact]
    public async Task GetRandomSongs_returns_songs_bounded_by_size()
    {
        var root = await XmlAsync("getRandomSongs?size=3");
        var songs = Descend(root, "song").ToList();
        Assert.InRange(songs.Count, 1, 3);
        Assert.All(songs, s => Assert.Equal("music", Attr(s, "type")));
    }

    [Fact]
    public async Task GetRandomSongs_by_genre_narrows_to_that_genre()
    {
        var root = await XmlAsync("getRandomSongs?size=50&genre=Pop");
        var titles = Descend(root, "song").Select(s => Attr(s, "title")).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "Sea", "Sun" }, titles);   // only the Pop compilation tracks
    }

    // ---- helpers ----

    private static void AssertFailed70(XElement root)
    {
        Assert.Equal("failed", Attr(root, "status"));
        var error = root.Element("error")!;
        Assert.Equal("70", Attr(error, "code"));
    }

    private async Task<string> ArtistIdOf(string name)
    {
        var root = await XmlAsync("getArtists?");
        var artist = Descend(root, "artist").Single(a => Attr(a, "name") == name);
        return Attr(artist, "id");
    }

    private async Task<string> AlbumIdOf(string artistName, string albumName)
    {
        var artistId = await ArtistIdOf(artistName);
        var root = await XmlAsync($"getArtist?id={artistId}");
        var album = Descend(root, "album").Single(a => Attr(a, "name") == albumName);
        return Attr(album, "id");
    }
}
