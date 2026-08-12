using Microsoft.AspNetCore.Mvc.Testing;

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

    public static void WriteTaggedTree(string rootsDir)
    {
        var albumDir = Path.Combine(rootsDir, "Alice - Debut");
        Directory.CreateDirectory(albumDir);
        var path = Path.Combine(albumDir, "01 - Intro.mp3");
        File.WriteAllBytes(path, SilentMp3());
        var t = new ATL.Track(path) { Artist = "Alice", Album = "Debut", Title = "Intro", TrackNumber = 1, Year = 2001 };
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

/// <summary>Its own non-parallel collection so the two factories' process-wide EBS_* env-var writes do
/// not race the other server factories (or each other — xUnit constructs a collection's fixtures
/// sequentially, and each pins its config by building its host in its constructor).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SubsonicServerCollection
    : ICollectionFixture<SubsonicServerFactory>, ICollectionFixture<SubsonicDisabledServerFactory>
{
    public const string Name = "subsonic-server";
}
