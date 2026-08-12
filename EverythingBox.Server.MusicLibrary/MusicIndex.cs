using EverythingBox.Server.Abstractions;   // SafeLocalFileServer.EncodeId
using System.Security.Cryptography;
using System.Text;

namespace EverythingBox.Server.MusicLibrary;

public sealed record MusicTrack(string Id, string Title, int? TrackNo, int? DiscNo, int? DurationSec, string Path, string ArtistName, string AlbumId);
public sealed record MusicAlbum(string Id, string ArtistId, string ArtistName, string Name, int? Year, string? CoverPath, IReadOnlyList<MusicTrack> Tracks);
public sealed record MusicArtist(string Id, string Name, IReadOnlyList<MusicAlbum> Albums);

public sealed class MusicIndex
{
    public IReadOnlyList<MusicArtist> Artists { get; }
    private readonly Dictionary<string, MusicArtist> _artistById;
    private readonly Dictionary<string, MusicAlbum> _albumById;
    private readonly Dictionary<string, MusicTrack> _trackById;   // trackId → track

    public MusicIndex(IReadOnlyList<MusicArtist> artists)
    {
        Artists = artists;
        _artistById = new Dictionary<string, MusicArtist>(StringComparer.Ordinal);
        _albumById = new Dictionary<string, MusicAlbum>(StringComparer.Ordinal);
        _trackById = new Dictionary<string, MusicTrack>(StringComparer.Ordinal);

        foreach (var artist in artists)
        {
            _artistById[artist.Id] = artist;
            foreach (var album in artist.Albums)
            {
                _albumById[album.Id] = album;
                foreach (var track in album.Tracks)
                    _trackById[track.Id] = track;
            }
        }
    }

    public MusicArtist? Artist(string id) => _artistById.GetValueOrDefault(id);
    public MusicAlbum? Album(string id) => _albumById.GetValueOrDefault(id);
    public MusicTrack? Track(string id) => _trackById.GetValueOrDefault(id);
    public static readonly MusicIndex Empty = new([]);

    // Stable, deterministic ids. Track/cover ids are base64url of the absolute path (so they round-trip
    // through the SafeLocalFileServer for serving). Artist/album ids are content hashes of the grouping
    // key so they survive a rescan (a path can't identify an album that spans files).
    public static string TrackId(string path) => SafeLocalFileServer.EncodeId(path);
    public static string ArtistId(string albumArtist) => "ar-" + Hash(albumArtist);
    public static string AlbumId(string albumArtist, string album) => "al-" + Hash(albumArtist + "\n" + album);
    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).ToLowerInvariant()[..16];
}
