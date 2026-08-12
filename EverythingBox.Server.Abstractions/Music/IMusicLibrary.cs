namespace EverythingBox.Server.Abstractions;

public sealed record MusicFolderInfo(string Id, string Name);
// Stars are DATETIMES, not bools: Subsonic renders a starred item as starred="<ISO8601>" (the instant it
// was starred) and omits the attribute entirely when unstarred. StarredAt carries that instant (null when
// not starred) across the DTOs.
public sealed record ArtistInfo(string Id, string Name, int AlbumCount, string? CoverArtId,
    DateTimeOffset? StarredAt = null);
public sealed record AlbumInfo(string Id, string Name, string ArtistId, string Artist,
    int? Year, string? Genre, int SongCount, int DurationSec, string? CoverArtId, DateTimeOffset? StarredAt);
public sealed record SongInfo(string Id, string Title, string AlbumId, string Album, string ArtistId, string Artist,
    int? Track, int? Disc, int? Year, string? Genre, int? DurationSec, string Suffix, string ContentType,
    long? SizeBytes, string? CoverArtId, DateTimeOffset? StarredAt);
public sealed record GenreInfo(string Name, int SongCount, int AlbumCount);
public sealed record PlaylistInfo(string Id, string Name, int SongCount, int DurationSec, IReadOnlyList<SongInfo> Songs);
public sealed record SearchResult(IReadOnlyList<ArtistInfo> Artists, IReadOnlyList<AlbumInfo> Albums, IReadOnlyList<SongInfo> Songs);

/// <summary>The music-domain surface a Subsonic/OpenSubsonic API serves from. Implemented by a
/// music-library plugin and registered via <see cref="IPluginRegistry.AddMusicLibrary"/>. All reads
/// are best-effort snapshots of the scanned library; the mutating calls persist small local state
/// (stars, listening history, playlists) — this server has no user model, so it is a single identity.</summary>
public interface IMusicLibrary
{
    IReadOnlyList<MusicFolderInfo> Folders();
    IReadOnlyList<ArtistInfo> Artists();
    /// <summary>An artist with its albums, or null if unknown.</summary>
    (ArtistInfo Artist, IReadOnlyList<AlbumInfo> Albums)? Artist(string id);
    /// <summary>An album with its songs, or null.</summary>
    (AlbumInfo Album, IReadOnlyList<SongInfo> Songs)? Album(string id);
    SongInfo? Song(string id);
    /// <summary>type ∈ newest/alphabeticalByName/alphabeticalByArtist/random/byYear/byGenre/recent/frequent/starred.
    /// Unknown types return empty (the route reports the honest error).</summary>
    IReadOnlyList<AlbumInfo> AlbumList(string type, int size, int offset, string? genre, int? fromYear, int? toYear);
    SearchResult Search(string query, int artistCount, int albumCount, int songCount);
    IReadOnlyList<SongInfo> RandomSongs(int size, string? genre);
    /// <summary>The distinct genres across the library with their song and album counts, for Subsonic
    /// getGenres. Empty when the library is untagged.</summary>
    IReadOnlyList<GenreInfo> Genres();

    /// <summary>The cover file for a cover-art id (album/artist/song), or null. path is a real file.</summary>
    (string Path, string ContentType)? CoverArt(string coverArtId);
    /// <summary>Serve a song's bytes with Range; null when the id is not a contained track.</summary>
    Task<ProxyResponse?> OpenTrackAsync(string songId, string? rangeHeader, CancellationToken ct);

    void Scrobble(string songId, DateTimeOffset playedAt);
    void SetStarred(string id, bool starred);
    /// <summary>The currently-starred artists/albums/songs (each carrying its <see cref="ArtistInfo.StarredAt"/>),
    /// for Subsonic getStarred2. Empty lists when nothing is starred.</summary>
    SearchResult Starred();
    IReadOnlyList<PlaylistInfo> Playlists();
    PlaylistInfo? Playlist(string id);
}
