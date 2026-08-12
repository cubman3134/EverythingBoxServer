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
public sealed class MusicLibrarySource : IMediaSource
{
    private const int MaxItems = 100_000;

    private readonly IReadOnlyList<string> _roots;
    private readonly string _coverCacheDir;
    private readonly LibraryMetaCache _meta;
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

    public MusicLibrarySource(IReadOnlyList<string> roots, string coverCacheDir, IResolverCache? cache, ILogger logger)
    {
        _roots = roots;
        _coverCacheDir = coverCacheDir;
        _logger = logger;
        _files = new SafeLocalFileServer([.. roots, coverCacheDir], MimeFor);
        _meta = new LibraryMetaCache(cache);

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
}
