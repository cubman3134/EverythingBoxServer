using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Tests;

/// <summary>Shared staging for the Subsonic host factories: copies the built <c>musiclib</c> plugin
/// (with ATL.dll + .deps.json) out of the test bin into a per-host plugins folder, and synthesizes a
/// single tagged MP3 under a temp Roots tree so the loaded plugin registers a non-empty
/// <see cref="EverythingBox.Server.Abstractions.IMusicLibrary"/> in DI — no committed binary fixture.</summary>
internal static class SubsonicHostStaging
{
    public static void StageMusicPlugin(string pluginsDirectory)
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", "musiclib");
        CopyDirectory(staged, Path.Combine(pluginsDirectory, "musiclib"));
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    // A minimal wholly-synthetic MPEG-1 Layer III stream: a silent 128kbps/44.1kHz frame repeated. ATL
    // detects it as an MP3 and writes a real ID3v2 tag over it on Save(), giving deterministic tagged
    // audio at runtime — SOURCE (a byte template), not a committed fixture. Mirrors MusicLibrarySourceTests.
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

    // Synthesizes a small but structurally complete tagged library so the read endpoints have artists to
    // index, albums with songs, genres to count, and — crucially — a COMPILATION (album-artist "Various
    // Artists", per-track performers) that the scanner collapses into one artist rather than one per
    // performer. Every file is a byte template ATL tags on Save() — no committed audio fixture.
    //   Alice          → Debut (2001, Rock)           : Intro
    //   Nova           → Nova Nights (2015, Jazz)      : Nova Theme, Second Star
    //                    Nova Dawn  (2018, Jazz)       : Dawn
    //   Various Artists→ Summer Hits (2020, Pop)       : Sun (by Carol), Sea (by Dave)
    public static void WriteTaggedTree(string rootsDir)
    {
        WriteTrack(rootsDir, "Alice - Debut", "01 - Intro.mp3",
            artist: "Alice", albumArtist: null, album: "Debut", title: "Intro", track: 1, year: 2001, genre: "Rock");

        WriteTrack(rootsDir, "Nova - Nova Nights", "01 - Nova Theme.mp3",
            artist: "Nova", albumArtist: null, album: "Nova Nights", title: "Nova Theme", track: 1, year: 2015, genre: "Jazz");
        WriteTrack(rootsDir, "Nova - Nova Nights", "02 - Second Star.mp3",
            artist: "Nova", albumArtist: null, album: "Nova Nights", title: "Second Star", track: 2, year: 2015, genre: "Jazz");
        WriteTrack(rootsDir, "Nova - Nova Dawn", "01 - Dawn.mp3",
            artist: "Nova", albumArtist: null, album: "Nova Dawn", title: "Dawn", track: 1, year: 2018, genre: "Jazz");

        WriteTrack(rootsDir, "Various - Summer Hits", "01 - Sun.mp3",
            artist: "Carol", albumArtist: "Various Artists", album: "Summer Hits", title: "Sun", track: 1, year: 2020, genre: "Pop");
        WriteTrack(rootsDir, "Various - Summer Hits", "02 - Sea.mp3",
            artist: "Dave", albumArtist: "Various Artists", album: "Summer Hits", title: "Sea", track: 2, year: 2020, genre: "Pop");
    }

    private static void WriteTrack(string rootsDir, string albumFolder, string file,
        string artist, string? albumArtist, string album, string title, int track, int year, string genre)
    {
        var albumDir = Path.Combine(rootsDir, albumFolder);
        Directory.CreateDirectory(albumDir);
        var path = Path.Combine(albumDir, file);
        File.WriteAllBytes(path, SilentMp3());
        var t = new ATL.Track(path)
        {
            Artist = artist, Album = album, Title = title, TrackNumber = track, Year = year, Genre = genre,
        };
        if (albumArtist is not null) t.AlbumArtist = albumArtist;
        Assert.True(t.Save(), "ATL failed to write tags to the synthesized fixture track");
    }
}

/// <summary>Boots the real host in-memory with the <c>musiclib</c> plugin loaded and Subsonic ENABLED,
/// an access token set (so the auth tests exercise the token path), and a temp Roots tree of synthesized
/// tagged audio. Env vars are set in the constructor and the host is forced to build immediately (a
/// <see cref="CreateClient"/> call) so its config read is atomic with its env-var write — the same
/// pattern as <see cref="PluginServerFactory"/> — before the sibling disabled factory's constructor can
/// overwrite the process-wide EBS_* vars.</summary>
public sealed class SubsonicServerFactory : WebApplicationFactory<Program>
{
    public const string Token = "subsonic-tok";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-subsonic-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");
    public string RootsDirectory => Path.Combine(_root, "music");

    /// <summary>Every formatted log line, so a test can assert Subsonic credentials (p / t / s) never reach
    /// the request log — the C1 leak: /rest carries credentials in the query, not the path.</summary>
    public List<string> LoggedMessages { get; } = [];

    public SubsonicServerFactory()
    {
        SubsonicHostStaging.StageMusicPlugin(PluginsDirectory);
        Directory.CreateDirectory(FilesDirectory);
        SubsonicHostStaging.WriteTaggedTree(RootsDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath,
            "{ \"AccessToken\": \"" + Token + "\", " +
            "\"Subsonic\": { \"Enabled\": true }, " +
            "\"Plugins\": { \"musiclib\": { \"Roots\": [" +
            System.Text.Json.JsonSerializer.Serialize(RootsDirectory) + "] } } }");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);
        Environment.SetEnvironmentVariable("EBS_SYNC_DIR", null);

        CreateClient().Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(LoggedMessages)));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>The same host, musiclib loaded, but with NO Subsonic config section — so
/// <see cref="ServerConfig.Subsonic"/> deserializes to disabled and MapSubsonic is never called. Used
/// to prove a disabled server has no <c>/rest</c> route at all (404, not an envelope).</summary>
public sealed class SubsonicDisabledServerFactory : WebApplicationFactory<Program>
{
    public const string Token = "subsonic-off-tok";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-subsonic-off-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");
    public string RootsDirectory => Path.Combine(_root, "music");

    public SubsonicDisabledServerFactory()
    {
        SubsonicHostStaging.StageMusicPlugin(PluginsDirectory);
        Directory.CreateDirectory(FilesDirectory);
        SubsonicHostStaging.WriteTaggedTree(RootsDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        // No "Subsonic" section at all — the absent section must deserialize to disabled.
        File.WriteAllText(configPath,
            "{ \"AccessToken\": \"" + Token + "\", " +
            "\"Plugins\": { \"musiclib\": { \"Roots\": [" +
            System.Text.Json.JsonSerializer.Serialize(RootsDirectory) + "] } } }");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);
        Environment.SetEnvironmentVariable("EBS_SYNC_DIR", null);

        CreateClient().Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>An <see cref="IMusicLibrary"/> whose every read throws — a stand-in for a misbehaving
/// plugin, so a test can prove the endpoint dispatch contains the throw in a code-0 envelope rather than
/// letting a raw 500 escape.</summary>
internal sealed class ThrowingMusicLibrary : EverythingBox.Server.Abstractions.IMusicLibrary
{
    private static InvalidOperationException Boom() => new("plugin blew up");

    public IReadOnlyList<EverythingBox.Server.Abstractions.MusicFolderInfo> Folders() => throw Boom();
    public IReadOnlyList<EverythingBox.Server.Abstractions.ArtistInfo> Artists() => throw Boom();
    public (EverythingBox.Server.Abstractions.ArtistInfo Artist, IReadOnlyList<EverythingBox.Server.Abstractions.AlbumInfo> Albums)? Artist(string id) => throw Boom();
    public (EverythingBox.Server.Abstractions.AlbumInfo Album, IReadOnlyList<EverythingBox.Server.Abstractions.SongInfo> Songs)? Album(string id) => throw Boom();
    public EverythingBox.Server.Abstractions.SongInfo? Song(string id) => throw Boom();
    public IReadOnlyList<EverythingBox.Server.Abstractions.AlbumInfo> AlbumList(string type, int size, int offset, string? genre, int? fromYear, int? toYear) => throw Boom();
    public EverythingBox.Server.Abstractions.SearchResult Search(string query, int artistCount, int albumCount, int songCount) => throw Boom();
    public IReadOnlyList<EverythingBox.Server.Abstractions.SongInfo> RandomSongs(int size, string? genre) => throw Boom();
    public IReadOnlyList<EverythingBox.Server.Abstractions.GenreInfo> Genres() => throw Boom();
    public (string Path, string ContentType)? CoverArt(string coverArtId) => throw Boom();
    public Task<EverythingBox.Server.Abstractions.ProxyResponse?> OpenTrackAsync(string songId, string? rangeHeader, CancellationToken ct) => throw Boom();
    public void Scrobble(string songId, DateTimeOffset playedAt) => throw Boom();
    public void SetStarred(string id, bool starred) => throw Boom();
    public IReadOnlyList<EverythingBox.Server.Abstractions.PlaylistInfo> Playlists() => throw Boom();
    public EverythingBox.Server.Abstractions.PlaylistInfo? Playlist(string id) => throw Boom();
}

/// <summary>The same enabled host, but overrides the DI <see cref="IMusicLibrary"/> with
/// <see cref="ThrowingMusicLibrary"/> (a later singleton wins), so every endpoint call throws — proving
/// the dispatch's try/catch containment. Pins its config in the constructor exactly like its siblings.</summary>
public sealed class SubsonicThrowingServerFactory : WebApplicationFactory<Program>
{
    public const string Token = "subsonic-throw-tok";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-subsonic-throw-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");
    public string RootsDirectory => Path.Combine(_root, "music");

    public SubsonicThrowingServerFactory()
    {
        SubsonicHostStaging.StageMusicPlugin(PluginsDirectory);
        Directory.CreateDirectory(FilesDirectory);
        SubsonicHostStaging.WriteTaggedTree(RootsDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath,
            "{ \"AccessToken\": \"" + Token + "\", " +
            "\"Subsonic\": { \"Enabled\": true }, " +
            "\"Plugins\": { \"musiclib\": { \"Roots\": [" +
            System.Text.Json.JsonSerializer.Serialize(RootsDirectory) + "] } } }");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);
        Environment.SetEnvironmentVariable("EBS_SYNC_DIR", null);

        CreateClient().Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureTestServices(services =>
            services.AddSingleton<EverythingBox.Server.Abstractions.IMusicLibrary>(new ThrowingMusicLibrary()));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>Its own non-parallel collection so the factories' process-wide EBS_* env-var writes do
/// not race the other server factories (or each other — xUnit constructs a collection's fixtures
/// sequentially, and each pins its config by building its host in its constructor).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SubsonicServerCollection
    : ICollectionFixture<SubsonicServerFactory>, ICollectionFixture<SubsonicDisabledServerFactory>,
      ICollectionFixture<SubsonicThrowingServerFactory>
{
    public const string Name = "subsonic-server";
}
