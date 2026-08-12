using System.Diagnostics;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.MusicLibrary;

namespace EverythingBox.Server.Tests;

public sealed class MusicScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ebmusic-" + Guid.NewGuid().ToString("N"));
    private readonly string _coverCache =
        Path.Combine(Path.GetTempPath(), "ebmusic-covers-" + Guid.NewGuid().ToString("N"));

    public MusicScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TryDelete(_root);
        TryDelete(_coverCache);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // A minimal, wholly-synthetic MPEG-1 Layer III stream: a silent 128kbps/44.1kHz frame repeated.
    // This is SOURCE (a byte template), not a committed binary fixture — ATL detects it as an MP3 and
    // writes a real ID3v2 tag over it on Save(), giving deterministic tagged audio at runtime.
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

    private static MusicScanner Scanner() => new();

    // ---- The classic bug: a compilation must NOT fragment into one artist per performer. ----
    [Fact]
    public async Task Compilation_groups_under_one_album_artist_with_one_album()
    {
        var dir = Path.Combine(_root, "VA - Greatest Hits");
        WriteTaggedTrack(dir, "01.mp3", artist: "Alice", albumArtist: "Various Artists", album: "Greatest Hits", title: "One", trackNo: 1);
        WriteTaggedTrack(dir, "02.mp3", artist: "Bob", albumArtist: "Various Artists", album: "Greatest Hits", title: "Two", trackNo: 2);
        WriteTaggedTrack(dir, "03.mp3", artist: "Carol", albumArtist: "Various Artists", album: "Greatest Hits", title: "Three", trackNo: 3);

        var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

        var artist = Assert.Single(index.Artists);
        Assert.Equal("Various Artists", artist.Name);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("Greatest Hits", album.Name);
        Assert.Equal(3, album.Tracks.Count);
    }

    [Fact]
    public async Task Tracks_sort_by_disc_then_track()
    {
        var dir = Path.Combine(_root, "Artist - Album");
        WriteTaggedTrack(dir, "a.mp3", artist: "Artist", album: "Album", title: "D2T1", trackNo: 1, discNo: 2);
        WriteTaggedTrack(dir, "b.mp3", artist: "Artist", album: "Album", title: "D1T2", trackNo: 2, discNo: 1);
        WriteTaggedTrack(dir, "c.mp3", artist: "Artist", album: "Album", title: "D1T1", trackNo: 1, discNo: 1);

        var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

        var album = index.Artists.Single().Albums.Single();
        Assert.Equal(["D1T1", "D1T2", "D2T1"], album.Tracks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public async Task Missing_album_artist_falls_back_to_artist()
    {
        var dir = Path.Combine(_root, "Solo");
        WriteTaggedTrack(dir, "x.mp3", artist: "Solo Artist", albumArtist: "", album: "Debut", title: "Song", trackNo: 1);

        var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

        var artist = Assert.Single(index.Artists);
        Assert.Equal("Solo Artist", artist.Name);
    }

    [Fact]
    public async Task Ids_round_trip_through_the_index()
    {
        var dir = Path.Combine(_root, "Band - Record");
        var path = WriteTaggedTrack(dir, "song.mp3", artist: "Band", album: "Record", title: "Hit", trackNo: 1, year: 1999);

        var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

        var artist = index.Artists.Single();
        var album = artist.Albums.Single();
        var track = album.Tracks.Single();

        Assert.Same(artist, index.Artist(artist.Id));
        Assert.Same(album, index.Album(album.Id));
        Assert.Same(track, index.Track(track.Id));
        Assert.Equal(track, index.Track(MusicIndex.TrackId(path)));
    }

    [Fact]
    public async Task Sibling_cover_is_picked_as_album_cover()
    {
        var dir = Path.Combine(_root, "Band - Art");
        WriteTaggedTrack(dir, "t.mp3", artist: "Band", album: "Art", title: "Song", trackNo: 1);
        var cover = Path.Combine(dir, "cover.jpg");
        File.WriteAllBytes(cover, [0xFF, 0xD8, 0xFF, 0xE0]); // arbitrary bytes; scanner only checks existence

        var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

        var album = index.Artists.Single().Albums.Single();
        Assert.Equal(cover, album.CoverPath);
    }

    [Fact]
    public async Task Second_scan_with_a_populated_cache_does_not_re_read_tags()
    {
        var dir = Path.Combine(_root, "Cached - Album");
        WriteTaggedTrack(dir, "1.mp3", artist: "Cached", album: "Album", title: "A", trackNo: 1);
        WriteTaggedTrack(dir, "2.mp3", artist: "Cached", album: "Album", title: "B", trackNo: 2);

        var cache = new CountingCache();
        var meta = new LibraryMetaCache(cache);

        var first = await Scanner().ScanAsync([_root], _coverCache, meta, CancellationToken.None);
        Assert.Equal(2, cache.Writes); // two misses → two computes (tag reads) → two writes

        var writesAfterFirst = cache.Writes;
        var second = await Scanner().ScanAsync([_root], _coverCache, meta, CancellationToken.None);

        // No new writes means every file was a cache hit — the tags were not re-read.
        Assert.Equal(writesAfterFirst, cache.Writes);
        Assert.Equal(first.Artists.Single().Albums.Single().Tracks.Count,
                     second.Artists.Single().Albums.Single().Tracks.Count);
    }

    [Fact]
    public async Task Corrupt_audio_file_does_not_throw_and_lists_under_unknown()
    {
        var dir = Path.Combine(_root, "Junk");
        Directory.CreateDirectory(dir);
        // A music extension over non-audio bytes: ATL will fail to parse; the scan must not throw.
        File.WriteAllBytes(Path.Combine(dir, "broken.mp3"), "this is not audio"u8.ToArray());

        var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

        var artist = Assert.Single(index.Artists);
        Assert.Equal("Unknown Artist", artist.Name);
        var album = Assert.Single(artist.Albums);
        Assert.Equal("Unknown Album", album.Name);
        Assert.Equal("broken", album.Tracks.Single().Title); // title falls back to the filename
    }

    // ---- C1 regression: the ATL dependency must be COPIED into the plugin's build output. ----
    // A plain classlib emits only the plugin dll + .deps.json; when deployed to plugins/musiclib/
    // the per-plugin AssemblyDependencyResolver can't resolve ATL and the plugin FileNotFounds at
    // first browse. EnableDynamicLoading (CopyLocalLockFileAssemblies) fixes it by copying ATL.dll
    // next to the plugin. Note: an in-process staged-load through PluginHost cannot catch this —
    // when the plugin's load context fails to resolve ATL it returns null and the DEFAULT context
    // resolves it (the test host already has ATL loaded via the ProjectReference), masking the very
    // failure production would hit. So assert the copy on disk directly.
    [Fact]
    public void ATL_dependency_is_copied_into_the_plugin_build_output()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory); // ...\Tests\bin\<Config>\<Tfm>\
        var tfm = baseDir.Name;                                    // e.g. net9.0
        var config = baseDir.Parent!.Name;                        // Debug | Release
        var repoRoot = baseDir.Parent!.Parent!.Parent!.Parent!.FullName; // up past bin\<Tests>

        var pluginOut = Path.Combine(repoRoot, "EverythingBox.Server.MusicLibrary", "bin", config, tfm);
        Assert.True(Directory.Exists(pluginOut),
            $"Could not locate the musiclib build output at {pluginOut}; adjust the path derivation if the layout changed.");

        // Sanity that this is the right directory (the plugin's own dll lives here).
        Assert.True(File.Exists(Path.Combine(pluginOut, "EverythingBox.Server.MusicLibrary.dll")),
            $"The musiclib plugin dll is missing from {pluginOut} — wrong directory or an unbuilt plugin.");

        Assert.True(File.Exists(Path.Combine(pluginOut, "ATL.dll")),
            $"ATL.dll is NOT next to the plugin in {pluginOut}. A musiclib deployed to plugins/musiclib/ will " +
            "FileNotFound on ATL at first browse. Ensure <EnableDynamicLoading>true</EnableDynamicLoading> is set " +
            "in EverythingBox.Server.MusicLibrary.csproj so the NuGet assemblies are copied to the plugin output.");
    }

    // ---- I1 regression: one reparse-point subdir must neither fault the scan nor leak escaped tracks. ----
    [Fact]
    public async Task Scan_skips_reparse_points_and_does_not_fault_on_a_junction()
    {
        var inside = Path.Combine(_root, "Local");
        WriteTaggedTrack(inside, "song.mp3", artist: "Local Band", album: "Local Album", title: "Local Song", trackNo: 1);

        // A tree OUTSIDE the configured root, reached only through a junction placed inside it.
        var outside = Path.Combine(Path.GetTempPath(), "ebmusic-outside-" + Guid.NewGuid().ToString("N"));
        WriteTaggedTrack(outside, "escaped.mp3", artist: "Escaped Band", album: "Escaped Album", title: "Escaped Song", trackNo: 1);

        try
        {
            var link = Path.Combine(_root, "link");
            MakeDirectoryLink(link, outside);
            // Sanity: the escaped track really is reachable THROUGH the junction (lexically under the root).
            Assert.True(File.Exists(Path.Combine(link, "escaped.mp3")));

            var index = await Scanner().ScanAsync([_root], _coverCache, new LibraryMetaCache(null), CancellationToken.None);

            // The walk skipped the reparse point: it completed (no IOException fault) and listed only
            // the real in-root track, never the junction-escaped one.
            var artist = Assert.Single(index.Artists);
            Assert.Equal("Local Band", artist.Name);
            Assert.DoesNotContain(index.Artists, a => a.Name == "Escaped Band");
        }
        finally
        {
            TryDelete(outside);
        }
    }

    /// <summary>Creates a directory reparse point at <paramref name="link"/> pointing at
    /// <paramref name="target"/>. On Windows a junction (mklink /J) needs no elevation; elsewhere a
    /// directory symlink is used (may require privilege on some platforms).</summary>
    private static void MakeDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new InvalidOperationException($"mklink /J failed: {p.StandardError.ReadToEnd()}");
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>An in-memory <see cref="IResolverCache"/> that counts writes, so a test can prove a
    /// second scan recomputed nothing (every file was a hit).</summary>
    private sealed class CountingCache : IResolverCache
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);
        public int Writes { get; private set; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _store[key] = value;
            Writes++;
            return Task.CompletedTask;
        }
    }
}
