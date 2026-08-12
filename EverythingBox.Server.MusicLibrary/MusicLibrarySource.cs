using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.MusicLibrary;

/// <summary>
/// The native "music" shelf. A <see cref="MusicScanner"/> aggregates the configured roots into a
/// <see cref="MusicIndex"/> (album-artist → album → track); the source projects that index as a
/// Subsonic-shaped hierarchy — <see cref="SearchAsync"/> lists the artists, <see cref="DetailAsync"/>
/// expands an artist into its albums and an album into its tracks, and <see cref="ResolveAsync"/> hands
/// back a proxy URL per track. Bytes — track files AND extracted/sibling cover images — are relayed with
/// HTTP Range through the host proxy route by a single <see cref="SafeLocalFileServer"/> whose roots are
/// the music roots PLUS the cover cache dir, so every id is decoded and confirmed inside a root before a
/// byte is served; an id is never trusted on its own. Track/cover ids are the encoded absolute path, so
/// they round-trip through that server; artist/album ids are content hashes the index resolves.
/// </summary>
public sealed class MusicLibrarySource : IMediaSource, IMusicLibrary
{
    private const int MaxItems = 100_000;

    private readonly IReadOnlyList<string> _roots;
    private readonly string _coverCacheDir;
    private readonly LibraryMetaCache _meta;
    private readonly MusicStateStore _store;
    private readonly ILogger _logger;

    // One server over the music roots AND the cover cache dir. Track files live under a root; a sibling
    // cover.* also lives under a root; an EXTRACTED embedded cover lives under the cache dir — so all
    // three are contained and OpenAsync serves them all through the same audited containment discipline.
    private readonly SafeLocalFileServer _files;

    // The index is built lazily on first use and cached. A SemaphoreSlim guards the build so a concurrent
    // browse can't trigger a second scan; the double-checked field keeps the hot path lock-free.
    private readonly MusicScanner _scanner = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private volatile MusicIndex? _index;

    public MusicLibrarySource(IReadOnlyList<string> roots, string coverCacheDir, IResolverCache? cache, ILogger logger,
        string? stateDir = null)
    {
        _roots = roots;
        _coverCacheDir = coverCacheDir;
        _logger = logger;
        _files = new SafeLocalFileServer([.. roots, coverCacheDir], MimeFor);
        _meta = new LibraryMetaCache(cache);
        _store = new MusicStateStore(stateDir);

        // The cover cache dir is one of _files' roots; SafeLocalFileServer resolves every root (following
        // reparse points) on each containment check and throws on a path that does not exist. The scanner
        // only creates it when it extracts an embedded cover, so a library with only sibling covers would
        // leave it missing and every serve would fault — create our own private cache dir up front.
        try { Directory.CreateDirectory(coverCacheDir); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not create cover cache dir {Dir}", coverCacheDir); }
    }

    public string Key => "musiclib";

    // A fresh checkout with no configured roots serves nothing.
    public IReadOnlyList<CatalogDescriptor> Catalogs
        => _roots.Count > 0 ? [new CatalogDescriptor("music", "Music", MediaTypeNames.Music)] : [];

    // Builds the index once and reuses it. Guarded so a concurrent browse doesn't double-scan.
    private async Task<MusicIndex> IndexAsync(CancellationToken ct)
    {
        if (_index is { } ready) return ready;

        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_index is { } cached) return cached;
            var built = await _scanner.ScanAsync(_roots, _coverCacheDir, _meta, ct).ConfigureAwait(false);
            _index = built;
            return built;
        }
        finally { _buildLock.Release(); }
    }

    // The proxy URL a client fetches for a cover: the id is the cover's own encoded absolute path, so
    // OpenAsync re-checks containment before serving a byte. Null cover → null url.
    private string? CoverUrl(string? coverPath) => coverPath is null
        ? null
        : $"proxy/{Key}/{SafeLocalFileServer.EncodeId(coverPath)}/{Uri.EscapeDataString(Path.GetFileName(coverPath))}";

    private string? ArtistThumb(MusicArtist artist)
        => CoverUrl(artist.Albums.Select(a => a.CoverPath).FirstOrDefault(c => c is not null));

    // The "music" catalog is the artists, optionally filtered by a name query. Artists-first is the
    // Subsonic-shaped default; each artist is expandable into its albums via DetailAsync.
    public async Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        if (catalogId != "music") return SourceCatalog.Empty("Music");

        var index = await IndexAsync(ct).ConfigureAwait(false);
        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var artist in index.Artists)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(query) &&
                !artist.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            if (items.Count >= MaxItems) { capped = true; break; }

            items.Add(new CatalogItem(
                Id: artist.Id,
                Title: artist.Name,
                Subtitle: string.Empty,
                Kind: MediaTypeNames.Music,
                ThumbnailUrl: ArtistThumb(artist),
                Expandable: true));
        }

        return new SourceCatalog("Music", items, capped);
    }

    // An artist id expands into its albums (expandable); an album id expands into its tracks (leaves).
    // Anything else — a track id, a foreign id — has nothing to expand → empty.
    public async Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        var index = await IndexAsync(ct).ConfigureAwait(false);

        if (index.Artist(itemId) is { } artist)
        {
            var albums = new List<CatalogItem>(artist.Albums.Count);
            foreach (var album in artist.Albums)
            {
                var title = album.Year is { } y && y > 0 ? $"{album.Name} ({y})" : album.Name;
                albums.Add(new CatalogItem(
                    Id: album.Id,
                    Title: title,
                    Subtitle: album.ArtistName,
                    Kind: MediaTypeNames.Music,
                    ThumbnailUrl: CoverUrl(album.CoverPath),
                    Expandable: true));
            }
            return new SourceCatalog(artist.Name, albums);
        }

        if (index.Album(itemId) is { } target)
        {
            var tracks = new List<CatalogItem>(target.Tracks.Count);
            foreach (var track in target.Tracks)
            {
                var title = track.TrackNo is { } n ? $"{n:D2}. {track.Title}" : track.Title;
                tracks.Add(new CatalogItem(
                    Id: track.Id,
                    Title: title,
                    Subtitle: $"{target.Name} · {track.ArtistName}",
                    Kind: MediaTypeNames.Music,
                    ThumbnailUrl: CoverUrl(target.CoverPath),
                    Expandable: false));
            }
            return new SourceCatalog(target.Name, tracks);
        }

        return SourceCatalog.Empty("Music");
    }

    // A track id resolves to a proxy URL the host serves via OpenAsync; the filename — with its
    // extension — is in the path, so the client keeps the codec hint. A non-track id → null.
    public async Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        var idx = await IndexAsync(ct).ConfigureAwait(false);
        if (idx.Track(itemId) is not { } track) return null;

        var url = $"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(track.Path))}";
        return new SourceStream(url, MimeFor(track.Path));
    }

    // Serves both track files and cover images — all contained by the roots-plus-cover-dir server, which
    // re-decodes and re-checks containment, so a forged/traversal id serves nothing.
    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => _files.OpenAsync(itemId, rangeHeader, ct);

    private static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "m4a" => "audio/mp4",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "wav" => "audio/wav",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            _ => "application/octet-stream",
        };
    }

    // ------------------------------------------------------------------------------------------------
    // IMusicLibrary — the Subsonic/OpenSubsonic domain surface. This projects the SAME lazily-built
    // MusicIndex the IMediaSource shelf browses (one scan, one cache) into the music DTOs, and routes
    // the mutating calls (stars/scrobbles/playlists) to the local MusicStateStore. The IMediaSource
    // behavior above is untouched: these are additional read projections plus the store, sharing nothing
    // mutable with the browse path. Cover-art ids are the encoded absolute cover path — identical to the
    // cover ids CoverUrl mints — so CoverArt() decodes and containment-checks them through the same
    // audited SafeLocalFileServer that serves them.
    // ------------------------------------------------------------------------------------------------

    // The synchronous DTO reads need the index in hand; the async build is blocked once here. A scan is
    // IO-bound and the result is cached, so this pays the cost at most once per source lifetime.
    private MusicIndex Index() => IndexAsync(CancellationToken.None).GetAwaiter().GetResult();

    private IEnumerable<MusicAlbum> AllAlbums() => Index().Artists.SelectMany(a => a.Albums);
    private IEnumerable<MusicTrack> AllTracks() => AllAlbums().SelectMany(a => a.Tracks);

    // A cover-art id is the cover file's own encoded absolute path (null when the album has no cover),
    // so it round-trips through _files exactly like a track id.
    private static string? CoverIdFor(string? coverPath)
        => coverPath is null ? null : SafeLocalFileServer.EncodeId(coverPath);

    private static long? SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private ArtistInfo ToArtistInfo(MusicArtist a)
        => new(a.Id, a.Name, a.Albums.Count,
            CoverIdFor(a.Albums.Select(al => al.CoverPath).FirstOrDefault(c => c is not null)),
            StarredAt: _store.StarredAt(a.Id));

    private AlbumInfo ToAlbumInfo(MusicAlbum a)
        => new(a.Id, a.Name, a.ArtistId, a.ArtistName, a.Year, Genre: a.Genre,
            SongCount: a.Tracks.Count, DurationSec: a.Tracks.Sum(t => t.DurationSec ?? 0),
            CoverArtId: CoverIdFor(a.CoverPath), StarredAt: _store.StarredAt(a.Id));

    private SongInfo ToSongInfo(MusicTrack t)
    {
        var album = Index().Album(t.AlbumId);
        return new SongInfo(
            Id: t.Id, Title: t.Title,
            AlbumId: t.AlbumId, Album: album?.Name ?? string.Empty,
            ArtistId: album?.ArtistId ?? string.Empty, Artist: t.ArtistName,
            // Year is the TRACK's own year (a mixed-year compilation keeps per-track years); the
            // album year stays the album-level value on AlbumInfo.
            Track: t.TrackNo, Disc: t.DiscNo, Year: t.Year, Genre: t.Genre,
            DurationSec: t.DurationSec,
            Suffix: Path.GetExtension(t.Path).TrimStart('.').ToLowerInvariant(),
            ContentType: MimeFor(t.Path),
            SizeBytes: SizeOf(t.Path),
            CoverArtId: CoverIdFor(album?.CoverPath),
            StarredAt: _store.StarredAt(t.Id));
    }

    private PlaylistInfo ToPlaylistInfo((string Id, string Name, IReadOnlyList<string> SongIds) p)
    {
        var songs = p.SongIds.Select(Song).Where(s => s is not null).Select(s => s!).ToList();
        return new PlaylistInfo(p.Id, p.Name, songs.Count, songs.Sum(s => s.DurationSec ?? 0), songs);
    }

    // v1 exposes the whole library as a single music folder.
    public IReadOnlyList<MusicFolderInfo> Folders() => [new MusicFolderInfo("1", "Music")];

    public IReadOnlyList<ArtistInfo> Artists() => Index().Artists.Select(ToArtistInfo).ToList();

    public (ArtistInfo Artist, IReadOnlyList<AlbumInfo> Albums)? Artist(string id)
        => Index().Artist(id) is { } a ? (ToArtistInfo(a), a.Albums.Select(ToAlbumInfo).ToList()) : null;

    public (AlbumInfo Album, IReadOnlyList<SongInfo> Songs)? Album(string id)
        => Index().Album(id) is { } a ? (ToAlbumInfo(a), a.Tracks.Select(ToSongInfo).ToList()) : null;

    public SongInfo? Song(string id) => Index().Track(id) is { } t ? ToSongInfo(t) : null;

    public IReadOnlyList<AlbumInfo> AlbumList(string type, int size, int offset, string? genre, int? fromYear, int? toYear)
    {
        var albums = AllAlbums();
        IEnumerable<MusicAlbum> ordered = type switch
        {
            "newest" => albums.OrderByDescending(a => a.Year ?? int.MinValue),
            "alphabeticalByName" => albums.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase),
            "alphabeticalByArtist" => albums.OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase),
            // A per-call shuffle: a fresh seed each call is exactly the Subsonic contract for "random".
            "random" => albums.OrderBy(_ => Guid.NewGuid()),
            "starred" => albums.Where(a => _store.IsStarred(a.Id)),
            // Albums whose (dominant) genre matches the requested one, case-insensitive. A byGenre
            // request with no genre matches nothing.
            "byGenre" => string.IsNullOrWhiteSpace(genre)
                ? []
                : albums.Where(a => string.Equals(a.Genre, genre, StringComparison.OrdinalIgnoreCase)),
            "byYear" => ByYear(albums, fromYear, toYear),
            // recent/frequent are honest projections of the local listening history when we have it.
            "recent" => ByHistory(albums, mostFrequent: false),
            "frequent" => ByHistory(albums, mostFrequent: true),
            _ => null!,
        };

        if (ordered is null) return [];   // unknown type → empty; the route reports the honest error.
        return ordered.Skip(Math.Max(0, offset)).Take(Math.Max(0, size)).Select(ToAlbumInfo).ToList();
    }

    private static IEnumerable<MusicAlbum> ByYear(IEnumerable<MusicAlbum> albums, int? fromYear, int? toYear)
    {
        var from = fromYear ?? int.MinValue;
        var to = toYear ?? int.MaxValue;
        var lo = Math.Min(from, to);
        var hi = Math.Max(from, to);
        var inRange = albums.Where(a => a.Year is { } y && y >= lo && y <= hi);
        // Subsonic reads a reversed from/to as a descending listing.
        return from <= to ? inRange.OrderBy(a => a.Year) : inRange.OrderByDescending(a => a.Year);
    }

    private IEnumerable<MusicAlbum> ByHistory(IEnumerable<MusicAlbum> albums, bool mostFrequent)
    {
        var byAlbum = Index();
        // Album id → its listen rows, derived from the per-song scrobble history.
        var rows = _store.Scrobbles()
            .Select(s => byAlbum.Track(s.SongId) is { } t ? (t.AlbumId, s.PlayedAt) : ((string, DateTimeOffset)?)null)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToList();
        if (rows.Count == 0) return [];

        var ranked = mostFrequent
            ? rows.GroupBy(r => r.Item1).Select(g => (Album: g.Key, Rank: (double)g.Count()))
            : rows.GroupBy(r => r.Item1).Select(g => (Album: g.Key, Rank: (double)g.Max(r => r.Item2).ToUnixTimeSeconds()));

        var order = ranked.OrderByDescending(x => x.Rank).Select(x => x.Album).ToList();
        return order.Select(id => albums.FirstOrDefault(a => a.Id == id)).Where(a => a is not null).Select(a => a!);
    }

    public SearchResult Search(string query, int artistCount, int albumCount, int songCount)
    {
        bool Match(string name) => name.Contains(query ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var artists = Index().Artists.Where(a => Match(a.Name)).Take(Math.Max(0, artistCount)).Select(ToArtistInfo).ToList();
        var albums = AllAlbums().Where(a => Match(a.Name)).Take(Math.Max(0, albumCount)).Select(ToAlbumInfo).ToList();
        var songs = AllTracks().Where(t => Match(t.Title)).Take(Math.Max(0, songCount)).Select(ToSongInfo).ToList();
        return new SearchResult(artists, albums, songs);
    }

    public IReadOnlyList<SongInfo> RandomSongs(int size, string? genre)
    {
        // A genre filter narrows to the tracks tagged with that genre (case-insensitive); no genre
        // shuffles the whole library.
        var tracks = string.IsNullOrWhiteSpace(genre)
            ? AllTracks()
            : AllTracks().Where(t => string.Equals(t.Genre, genre, StringComparison.OrdinalIgnoreCase));
        return tracks.OrderBy(_ => Guid.NewGuid()).Take(Math.Max(0, size)).Select(ToSongInfo).ToList();
    }

    public IReadOnlyList<GenreInfo> Genres()
    {
        // Distinct genres across the library, each with its song and album counts. A genre is counted
        // once per album whose (dominant) genre matches; songs are counted per tagged track. Names are
        // grouped case-insensitively but reported with their first-seen casing, sorted by name.
        var songsByGenre = AllTracks()
            .Where(t => !string.IsNullOrEmpty(t.Genre))
            .GroupBy(t => t.Genre!, StringComparer.OrdinalIgnoreCase);
        var albumCounts = AllAlbums()
            .Where(a => !string.IsNullOrEmpty(a.Genre))
            .GroupBy(a => a.Genre!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return songsByGenre
            .Select(g => new GenreInfo(g.Key, g.Count(), albumCounts.GetValueOrDefault(g.Key)))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public (string Path, string ContentType)? CoverArt(string coverArtId)
    {
        // A cover id is an encoded absolute path; decode + containment-check it through the same server
        // that serves it, then only hand back genuine image files.
        if (_files.ResolveSafeFile(coverArtId) is not { } path) return null;
        var mime = MimeFor(path);
        return mime.StartsWith("image/", StringComparison.Ordinal) ? (path, mime) : null;
    }

    public async Task<ProxyResponse?> OpenTrackAsync(string songId, string? rangeHeader, CancellationToken ct)
    {
        // Gate on the id being a known track so a forged/foreign contained id serves nothing here.
        var idx = await IndexAsync(ct).ConfigureAwait(false);
        if (idx.Track(songId) is null) return null;
        return await _files.OpenAsync(songId, rangeHeader, ct).ConfigureAwait(false);
    }

    public void Scrobble(string songId, DateTimeOffset playedAt) => _store.Scrobble(songId, playedAt);

    public void SetStarred(string id, bool starred) => _store.SetStarred(id, starred);

    // getStarred2: the starred artists/albums/songs across the library, each carrying its StarredAt. The
    // store holds ids only, so we walk the index and keep whatever the store still marks starred (a star on
    // an id that no longer resolves — a since-deleted file — simply drops out).
    public SearchResult Starred()
    {
        var artists = Index().Artists.Where(a => _store.IsStarred(a.Id)).Select(ToArtistInfo).ToList();
        var albums = AllAlbums().Where(a => _store.IsStarred(a.Id)).Select(ToAlbumInfo).ToList();
        var songs = AllTracks().Where(t => _store.IsStarred(t.Id)).Select(ToSongInfo).ToList();
        return new SearchResult(artists, albums, songs);
    }

    public IReadOnlyList<PlaylistInfo> Playlists() => _store.Playlists().Select(ToPlaylistInfo).ToList();

    public PlaylistInfo? Playlist(string id) => _store.Playlist(id) is { } p ? ToPlaylistInfo(p) : null;
}
