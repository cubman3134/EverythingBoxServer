using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace EverythingBox.Server.Subsonic;

public static class SubsonicEndpoints
{
    /// <summary>Maps the Subsonic <c>/rest</c> surface. Deliberately mounted at a BARE <c>/rest</c> with
    /// NO token prefix — unlike every other route on this host, Subsonic authenticates per request
    /// (see <see cref="SubsonicAuth"/>) against the server access token, so it is the first surface the
    /// token does not gate at the URL. Only mapped when Subsonic is enabled AND a music library is
    /// present (see Program.cs).</summary>
    public static void MapSubsonic(this WebApplication app)
    {
        app.MapMethods("/rest/{endpoint}", ["GET", "POST"],
            (string endpoint, HttpContext http, IMusicLibrary music, ServerConfig config, ILoggerFactory loggerFactory) =>
            {
                // Clients hit either /rest/ping or /rest/ping.view — strip a trailing ".view".
                var name = endpoint.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ? endpoint[..^5] : endpoint;

                if (!SubsonicAuth.Authenticate(http.Request, config.AccessToken))
                    return SubsonicResponse.Error(http.Request, 40, "Wrong username or password.");

                var req = http.Request;
                try
                {
                    return name switch
                    {
                        "ping" => SubsonicResponse.Ok(req, null),
                        "getLicense" => SubsonicResponse.Ok(req, new SubsonicNode("license").AttrBool("valid", true)),

                        // ---- Increment 3 read endpoints. Each reads params via SubsonicParams (query, or a
                        // form POST body), calls IMusicLibrary, wraps a node payload in Ok; a bad/unknown id →
                        // 70, a missing required param → 10. ----
                        "getMusicFolders" => SubsonicResponse.Ok(req, MusicFolders(music)),
                        "getIndexes" => SubsonicResponse.Ok(req, Indexes("indexes", music)),
                        "getArtists" => SubsonicResponse.Ok(req, Indexes("artists", music)),
                        "getArtist" => GetArtist(req, music),
                        "getAlbum" => GetAlbum(req, music),
                        "getSong" => GetSong(req, music),
                        "getAlbumList2" => GetAlbumList2(req, music),
                        "getGenres" => GetGenres(req, music),
                        "search3" => Search3(req, music),
                        "getRandomSongs" => GetRandomSongs(req, music),

                        // Media streaming (stream/download/getCoverArt) + writes (star/scrobble/playlists)
                        // arrive in Increment 4.
                        _ => SubsonicResponse.Error(req, 0, $"Endpoint not implemented: {name}"),
                    };
                }
                catch (Exception ex)
                {
                    // Plugin-authored IMusicLibrary can throw; contain it to a code-0 Subsonic envelope rather
                    // than leaking a raw 500 (matches the host's plugin-containment discipline). The endpoint
                    // name is not a credential, but nothing else about the request is logged.
                    loggerFactory.CreateLogger("Subsonic").LogWarning(ex, "Subsonic endpoint {Endpoint} threw", name);
                    return SubsonicResponse.Error(req, 0, "Server error");
                }
            });
    }

    // ---------------------------------------------------------------------------------------------
    // Node builders: IMusicLibrary DTO → SubsonicNode, carrying the exact Subsonic element/attribute
    // names. Attr() drops nulls, so every optional (coverArt/year/genre/track/…) simply omits itself.
    // ---------------------------------------------------------------------------------------------

    // ids and names STAY strings (Attr) even when all-digit — a JSON id must remain quoted. Counts,
    // durations, years, track/disc and size are AttrNum (raw JSON numbers); isDir is AttrBool.
    private static SubsonicNode Artist(ArtistInfo a) =>
        new SubsonicNode("artist")
            .Attr("id", a.Id)
            .Attr("name", a.Name)
            .AttrNum("albumCount", a.AlbumCount)
            .Attr("coverArt", a.CoverArtId);

    private static SubsonicNode Album(AlbumInfo a) =>
        new SubsonicNode("album")
            .Attr("id", a.Id)
            .Attr("name", a.Name)
            .Attr("artist", a.Artist)
            .Attr("artistId", a.ArtistId)
            .Attr("coverArt", a.CoverArtId)
            .AttrNum("songCount", a.SongCount)
            .AttrNum("duration", a.DurationSec)
            .AttrNum("year", a.Year)
            .Attr("genre", a.Genre);

    private static SubsonicNode Song(SongInfo s) =>
        new SubsonicNode("song")
            .Attr("id", s.Id)
            .Attr("parent", s.AlbumId)
            .Attr("title", s.Title)
            .Attr("album", s.Album)
            .Attr("artist", s.Artist)
            .Attr("artistId", s.ArtistId)
            .Attr("albumId", s.AlbumId)
            .Attr("coverArt", s.CoverArtId)
            .AttrNum("size", s.SizeBytes)
            .AttrNum("duration", s.DurationSec)
            .AttrNum("track", s.Track)
            .AttrNum("discNumber", s.Disc)
            .AttrNum("year", s.Year)
            .Attr("genre", s.Genre)
            .Attr("suffix", s.Suffix)
            .Attr("contentType", s.ContentType)
            .AttrBool("isDir", false)
            .Attr("type", "music");

    // Subsonic quirk: a <genre> carries its NAME as the element TEXT node (not an attribute), while
    // songCount/albumCount are numeric attributes. The dual renderer maps .Text → XML text / JSON "value".
    private static SubsonicNode Genre(GenreInfo g)
    {
        var node = new SubsonicNode("genre")
            .AttrNum("songCount", g.SongCount)
            .AttrNum("albumCount", g.AlbumCount);
        node.Text = g.Name;
        return node;
    }

    // ---------------------------------------------------------------------------------------------
    // Endpoints.
    // ---------------------------------------------------------------------------------------------

    private static SubsonicNode MusicFolders(IMusicLibrary music)
    {
        var root = new SubsonicNode("musicFolders");
        foreach (var f in music.Folders())
            root.Add(new SubsonicNode("musicFolder").Attr("id", f.Id).Attr("name", f.Name));
        return root;
    }

    // getArtists → <artists>, getIndexes → <indexes>: the same first-letter grouping under both roots.
    // Each artist's index letter is the uppercased first letter of its name; anything non-alphabetic
    // (a digit, a symbol) falls under "#".
    private static SubsonicNode Indexes(string rootName, IMusicLibrary music)
    {
        var root = new SubsonicNode(rootName);
        var groups = music.Artists()
            .GroupBy(a => IndexLetter(a.Name))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var index = new SubsonicNode("index").Attr("name", group.Key);
            foreach (var a in group.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                index.Add(Artist(a));
            root.Add(index);
        }
        return root;
    }

    private static string IndexLetter(string name)
    {
        var c = name.TrimStart().FirstOrDefault();
        return char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : "#";
    }

    private static IResult GetArtist(HttpRequest req, IMusicLibrary music)
    {
        if (RequireId(req, out var id, out var missing)) return missing!;
        if (music.Artist(id) is not { } result) return SubsonicResponse.Error(req, 70, "not found");

        var node = Artist(result.Artist);
        foreach (var album in result.Albums) node.Add(Album(album));
        return SubsonicResponse.Ok(req, node);
    }

    private static IResult GetAlbum(HttpRequest req, IMusicLibrary music)
    {
        if (RequireId(req, out var id, out var missing)) return missing!;
        if (music.Album(id) is not { } result) return SubsonicResponse.Error(req, 70, "not found");

        var node = Album(result.Album);
        foreach (var song in result.Songs) node.Add(Song(song));
        return SubsonicResponse.Ok(req, node);
    }

    private static IResult GetSong(HttpRequest req, IMusicLibrary music)
    {
        if (RequireId(req, out var id, out var missing)) return missing!;
        return music.Song(id) is { } song
            ? SubsonicResponse.Ok(req, Song(song))
            : SubsonicResponse.Error(req, 70, "not found");
    }

    private static IResult GetAlbumList2(HttpRequest req, IMusicLibrary music)
    {
        var type = SubsonicParams.Get(req, "type").ToString();
        if (string.IsNullOrEmpty(type)) type = "alphabeticalByName";
        var size = Math.Clamp(ParseInt(SubsonicParams.Get(req, "size"), 10), 0, 500);   // Subsonic caps size at 500.
        var offset = Math.Max(0, ParseInt(SubsonicParams.Get(req, "offset"), 0));
        var genre = Blank(SubsonicParams.Get(req, "genre"));
        var fromYear = ParseIntOrNull(SubsonicParams.Get(req, "fromYear"));
        var toYear = ParseIntOrNull(SubsonicParams.Get(req, "toYear"));

        var albums = music.AlbumList(type, size, offset, genre, fromYear, toYear);
        var root = new SubsonicNode("albumList2");
        foreach (var a in albums) root.Add(Album(a));
        return SubsonicResponse.Ok(req, root);
    }

    private static IResult GetGenres(HttpRequest req, IMusicLibrary music)
    {
        var root = new SubsonicNode("genres");
        foreach (var g in music.Genres()) root.Add(Genre(g));
        return SubsonicResponse.Ok(req, root);
    }

    private static IResult Search3(HttpRequest req, IMusicLibrary music)
    {
        var query = SubsonicParams.Get(req, "query").ToString();   // may be "" for browse-all; the library caps the result.
        var artistCount = ParseInt(SubsonicParams.Get(req, "artistCount"), 20);
        var albumCount = ParseInt(SubsonicParams.Get(req, "albumCount"), 20);
        var songCount = ParseInt(SubsonicParams.Get(req, "songCount"), 20);

        var result = music.Search(query, artistCount, albumCount, songCount);
        var root = new SubsonicNode("searchResult3");
        foreach (var a in result.Artists) root.Add(Artist(a));
        foreach (var a in result.Albums) root.Add(Album(a));
        foreach (var s in result.Songs) root.Add(Song(s));
        return SubsonicResponse.Ok(req, root);
    }

    private static IResult GetRandomSongs(HttpRequest req, IMusicLibrary music)
    {
        var size = Math.Clamp(ParseInt(SubsonicParams.Get(req, "size"), 10), 0, 500);   // clamp high too, like getAlbumList2.
        var genre = Blank(SubsonicParams.Get(req, "genre"));

        var root = new SubsonicNode("randomSongs");
        foreach (var s in music.RandomSongs(size, genre)) root.Add(Song(s));
        return SubsonicResponse.Ok(req, root);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    // True when the required `id` is missing — sets `missing` to the code-10 envelope. Otherwise false
    // and `id` holds the value.
    private static bool RequireId(HttpRequest req, out string id, out IResult? missing)
    {
        id = SubsonicParams.Get(req, "id").ToString();
        if (string.IsNullOrEmpty(id))
        {
            missing = SubsonicResponse.Error(req, 10, "Required parameter is missing.");
            return true;
        }
        missing = null;
        return false;
    }

    private static string? Blank(StringValues v)
    {
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static int ParseInt(StringValues v, int fallback)
        => int.TryParse(v.ToString(), out var n) ? n : fallback;

    private static int? ParseIntOrNull(StringValues v)
        => int.TryParse(v.ToString(), out var n) ? n : null;
}
