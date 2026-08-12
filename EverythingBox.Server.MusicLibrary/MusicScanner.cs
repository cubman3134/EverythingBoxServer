using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.MusicLibrary;

/// <summary>
/// Walks configured roots, reads audio tags (via ATL), and aggregates them into a
/// <see cref="MusicIndex"/> grouped by ALBUM-ARTIST — so a compilation (many performers, one
/// "Various Artists" album-artist) collapses into a single album rather than fragmenting into
/// one artist per track. Tag reads are memoized by (path, mtime) through <see cref="LibraryMetaCache"/>,
/// so a rescan of an unchanged file never re-parses it. A read failure never escapes a scan: the
/// file still lists, under "Unknown Artist"/its filename.
/// </summary>
public sealed class MusicScanner
{
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".ogg", ".opus", ".wav" };

    private static readonly HashSet<string> CoverExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    private static readonly string[] CoverBaseNames = ["cover", "folder"];

    // Defensive cap so a pathological tree can't build an unbounded index.
    private const int MaxTracks = 100_000;

    // Mirrors LocalLibrarySource's hardened walk. RecurseSubdirectories with IgnoreInaccessible
    // means one unreadable subfolder is skipped instead of throwing mid-enumeration and faulting
    // the whole index (which would then re-fault on every browse). AttributesToSkip = ONLY
    // ReparsePoint keeps junctions/symlinks from being descended — no path-length blow-up from a
    // junction cycle, and no list/serve divergence from a link escaping the configured root — while
    // still listing files the owner incidentally marked Hidden/System (the framework default skips
    // those, which is wrong for a media shelf).
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    /// <summary>The subset of tag fields the index needs, memoized per file. A read failure yields an
    /// all-null instance (never thrown), so the file still lists under Unknown.</summary>
    internal sealed record TrackTags(
        string? Artist, string? AlbumArtist, string? Album, string? Title,
        int? TrackNo, int? DiscNo, int? Year, string? Genre, int? DurationSec, bool HasEmbeddedCover);

    public async Task<MusicIndex> ScanAsync(
        IReadOnlyList<string> roots, string coverCacheDir, LibraryMetaCache cache, CancellationToken ct)
    {
        // path → its tags, preserving discovery so identical (artist/album) files aggregate.
        var files = new List<(string Path, TrackTags Tags)>();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(root, "*", WalkOptions); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!AudioExtensions.Contains(Path.GetExtension(path))) continue;
                if (files.Count >= MaxTracks) break;

                var tags = await cache.GetOrComputeAsync<TrackTags>(path, null, () => ReadTags(path), ct)
                                      .ConfigureAwait(false);
                files.Add((path, tags));
            }
        }

        // Group by album-artist, then by album. The album-artist grouping is the whole point:
        // it is what keeps a compilation as one album instead of one artist per performer.
        var byArtist = new Dictionary<string, ArtistBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, tags) in files)
        {
            var albumArtist = Blank(tags.AlbumArtist) ?? Blank(tags.Artist) ?? "Unknown Artist";
            var album = Blank(tags.Album) ?? "Unknown Album";
            var title = Blank(tags.Title) ?? Path.GetFileNameWithoutExtension(path);

            if (!byArtist.TryGetValue(albumArtist, out var artistBuilder))
                byArtist[albumArtist] = artistBuilder = new ArtistBuilder(albumArtist);

            var albumId = MusicIndex.AlbumId(albumArtist, album);
            if (!artistBuilder.Albums.TryGetValue(albumId, out var albumBuilder))
                artistBuilder.Albums[albumId] = albumBuilder =
                    new AlbumBuilder(albumId, artistBuilder.Id, albumArtist, album);

            albumBuilder.Year ??= tags.Year;
            var trackArtist = Blank(tags.Artist) ?? albumArtist;
            albumBuilder.Tracks.Add(new MusicTrack(
                MusicIndex.TrackId(path), title, tags.TrackNo, tags.DiscNo, tags.DurationSec,
                path, trackArtist, albumId));

            if (tags.HasEmbeddedCover) albumBuilder.EmbeddedCoverSource ??= path;
        }

        var artists = new List<MusicArtist>(byArtist.Count);
        foreach (var artistBuilder in byArtist.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            var albums = new List<MusicAlbum>(artistBuilder.Albums.Count);
            foreach (var albumBuilder in artistBuilder.Albums.Values
                         .OrderBy(a => a.Year ?? int.MaxValue)
                         .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var tracks = albumBuilder.Tracks
                    .OrderBy(t => t.DiscNo ?? 1)
                    .ThenBy(t => t.TrackNo ?? 0)
                    .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var cover = ResolveCover(albumBuilder, coverCacheDir);

                albums.Add(new MusicAlbum(
                    albumBuilder.Id, albumBuilder.ArtistId, albumBuilder.ArtistName,
                    albumBuilder.Name, albumBuilder.Year, cover, tracks));
            }

            artists.Add(new MusicArtist(artistBuilder.Id, artistBuilder.Name, albums));
        }

        return new MusicIndex(artists);
    }

    /// <summary>Reads the tag subset for one file. Any failure → an all-null <see cref="TrackTags"/>;
    /// a scan never throws because a single file is unreadable or corrupt.</summary>
    private static TrackTags ReadTags(string path)
    {
        try
        {
            var t = new ATL.Track(path);
            return new TrackTags(
                Artist: t.Artist,
                AlbumArtist: t.AlbumArtist,
                Album: t.Album,
                Title: t.Title,
                TrackNo: t.TrackNumber,
                DiscNo: t.DiscNumber,
                Year: t.Year,
                Genre: t.Genre,
                DurationSec: t.Duration,
                HasEmbeddedCover: t.EmbeddedPictures.Count > 0);
        }
        catch
        {
            return new TrackTags(null, null, null, null, null, null, null, null, null, false);
        }
    }

    /// <summary>Cover for an album, first hit wins: a sibling cover.*/folder.* in a track's directory;
    /// else the first embedded picture, extracted ONCE to {coverCacheDir}/{albumId}.{ext}; else null.</summary>
    private static string? ResolveCover(AlbumBuilder album, string coverCacheDir)
    {
        // Sibling image next to a track. An album can span directories; check each distinct one.
        foreach (var dir in album.Tracks.Select(t => Path.GetDirectoryName(t.Path))
                                  .Where(d => !string.IsNullOrEmpty(d))
                                  .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var baseName in CoverBaseNames)
            foreach (var ext in CoverExtensions)
            {
                var candidate = Path.Combine(dir!, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Embedded picture, extracted once and cached on disk.
        if (album.EmbeddedCoverSource is { } source)
        {
            try
            {
                var track = new ATL.Track(source);
                if (track.EmbeddedPictures.Count > 0)
                {
                    var pic = track.EmbeddedPictures[0];
                    var ext = ExtensionForMime(pic.MimeType);
                    Directory.CreateDirectory(coverCacheDir);
                    var target = Path.Combine(coverCacheDir, album.Id + ext);

                    if (!File.Exists(target))
                    {
                        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
                        File.WriteAllBytes(tmp, pic.PictureData);
                        // temp-then-move: a reader never sees a half-written cover.
                        File.Move(tmp, target, overwrite: true);
                    }
                    return target;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* no cover rather than a failed scan */ }
        }

        return null;
    }

    private static string ExtensionForMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/gif" => ".gif",
        _ => ".jpg",
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class ArtistBuilder(string name)
    {
        public string Id { get; } = MusicIndex.ArtistId(name);
        public string Name { get; } = name;
        public Dictionary<string, AlbumBuilder> Albums { get; } = new(StringComparer.Ordinal);
    }

    private sealed class AlbumBuilder(string id, string artistId, string artistName, string name)
    {
        public string Id { get; } = id;
        public string ArtistId { get; } = artistId;
        public string ArtistName { get; } = artistName;
        public string Name { get; } = name;
        public int? Year { get; set; }
        public string? EmbeddedCoverSource { get; set; }
        public List<MusicTrack> Tracks { get; } = [];
    }
}
