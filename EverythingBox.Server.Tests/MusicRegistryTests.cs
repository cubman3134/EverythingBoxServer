using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;

namespace EverythingBox.Server.Tests;

file sealed class FakeMusicLibrary : IMusicLibrary
{
    public IReadOnlyList<MusicFolderInfo> Folders() => [];
    public IReadOnlyList<ArtistInfo> Artists() => [];
    public (ArtistInfo Artist, IReadOnlyList<AlbumInfo> Albums)? Artist(string id) => null;
    public (AlbumInfo Album, IReadOnlyList<SongInfo> Songs)? Album(string id) => null;
    public SongInfo? Song(string id) => null;
    public IReadOnlyList<AlbumInfo> AlbumList(string type, int size, int offset, string? genre, int? fromYear, int? toYear) => [];
    public SearchResult Search(string query, int artistCount, int albumCount, int songCount) => new([], [], []);
    public IReadOnlyList<SongInfo> RandomSongs(int size, string? genre) => [];
    public (string Path, string ContentType)? CoverArt(string coverArtId) => null;
    public Task<ProxyResponse?> OpenTrackAsync(string songId, string? rangeHeader, CancellationToken ct) => Task.FromResult<ProxyResponse?>(null);
    public void Scrobble(string songId, DateTimeOffset playedAt) { }
    public void SetStarred(string id, bool starred) { }
    public IReadOnlyList<PlaylistInfo> Playlists() => [];
    public PlaylistInfo? Playlist(string id) => null;
}

public class MusicRegistryTests
{
    [Fact]
    public void A_plugin_can_supply_a_music_library()
    {
        var registry = new PluginRegistry();
        registry.AddMusicLibrary(new FakeMusicLibrary());
        Assert.NotNull(registry.MusicLibrary);
    }

    [Fact]
    public void A_fresh_registry_has_no_music_library()
        => Assert.Null(new PluginRegistry().MusicLibrary);

    [Fact]
    public void Rejects_a_null_music_library()
        => Assert.Throws<ArgumentNullException>(() => new PluginRegistry().AddMusicLibrary(null!));

    [Fact]
    public void A_second_music_library_registration_throws()
    {
        // Only one music library can back the Subsonic surface server-wide. Two plugins both
        // supplying one is a real configuration mistake, so this is a throw (same as
        // AddProviderTracker) rather than a silent replace that would leave whoever registered
        // the first library wondering why it's never consulted.
        var registry = new PluginRegistry();
        registry.AddMusicLibrary(new FakeMusicLibrary());

        var ex = Assert.Throws<InvalidOperationException>(() => registry.AddMusicLibrary(new FakeMusicLibrary()));
        Assert.Contains("already registered", ex.Message);

        // And the first registration is left untouched.
        Assert.NotNull(registry.MusicLibrary);
    }
}
