using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Sources;

/// <summary>
/// Exposes every configured indexer (config-driven and plugin-provided alike) as
/// search-only catalogs, one per media type the pipeline understands. This is what
/// makes a stock server useful out of the box: no plugin is required to get a
/// searchable library, because <see cref="ITorrentGrabber"/> already knows every
/// indexer regardless of where it came from.
/// <para>
/// This is an ordinary <see cref="IMediaSource"/> — <c>SourceRouter</c> reaches it the
/// same way it reaches every other source, by splitting "idx:{payload}" on the colon.
/// There is no special routing here or anywhere else.
/// </para>
/// </summary>
public sealed class IndexerSearchSource : IMediaSource
{
    private readonly ITorrentGrabber _grabber;
    private readonly ReleaseStreamResolver _resolver;
    private readonly ILogger<IndexerSearchSource> _logger;

    public IndexerSearchSource(ITorrentGrabber grabber, ReleaseStreamResolver resolver, ILogger<IndexerSearchSource> logger)
    {
        _grabber = grabber;
        _resolver = resolver;
        _logger = logger;
    }

    public string Key => "idx";

    // "MediaType" on each descriptor is the addon-protocol string — the same
    // vocabulary MediaTypeNames translates to/from the pipeline's MediaType enum.
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } =
    [
        new CatalogDescriptor("movies", "Movies (search)", "movie"),
        new CatalogDescriptor("series", "Series (search)", "series"),
        new CatalogDescriptor("music", "Music (search)", "music"),
        new CatalogDescriptor("audiobooks", "Audiobooks (search)", "audiobook"),
        new CatalogDescriptor("books", "Books (search)", "book"),
        new CatalogDescriptor("comics", "Comics (search)", "comic"),
    ];

    // movie/series are built into the client and must NOT be declared here — only the
    // types the client doesn't know natively need presentation hints, or they render
    // as an unlabelled shelf.
    public IReadOnlyList<MediaTypeDescriptor> MediaTypes { get; } =
    [
        new MediaTypeDescriptor("music", "#C0392B", "\U0001F3B5", "audio", "poster"),
        new MediaTypeDescriptor("audiobook", "#5B7FE0", "\U0001F3A7", "audio", "poster"),
        new MediaTypeDescriptor("book", "#3E8E7E", "\U0001F4D7", "document", "poster"),
        new MediaTypeDescriptor("comic", "#E07A2E", "\U0001F4DA", "document", "poster"),
    ];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => SearchAsyncCore(catalogId, query, ct);

    private async Task<SourceCatalog> SearchAsyncCore(string catalogId, string? query, CancellationToken ct)
    {
        var descriptor = FindCatalog(catalogId);
        if (descriptor is null)
            return SourceCatalog.Empty("Search");

        // Search-only shelves have nothing to browse — firing a blank search at every
        // configured indexer every time a user opens the catalog would be wasteful and
        // would hammer indexers for no benefit.
        if (string.IsNullOrWhiteSpace(query))
            return new SourceCatalog($"Search {descriptor.Name} to see results", []);

        if (!MediaTypeNames.TryParseProtocol(descriptor.MediaType, out var mediaType))
            return SourceCatalog.Empty(descriptor.Name);

        var request = BuildRequest(mediaType, query);

        IReadOnlyList<TorrentResult> results;
        try
        {
            results = await _grabber.SearchAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // An unreachable/misbehaving indexer must degrade to an empty shelf, never a
            // failed request — the same containment discipline every other source-facing
            // call in this host follows.
            _logger.LogWarning(ex,
                "Indexer search for '{Query}' on catalog '{Catalog}' failed; returning an empty catalog.",
                query, catalogId);
            return SourceCatalog.Empty(descriptor.Name);
        }

        var items = results
            .Select(result => ToItem(result, descriptor.MediaType))
            .ToList();

        return new SourceCatalog(descriptor.Name, items);
    }

    // A release does not expand into anything further — there is no season/episode
    // structure or table of contents to reveal here, only a single downloadable thing.
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("Release"));

    public async Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (DecodeRelease(itemId) is not { } decoded)
            return null;

        // The decoded id carries no season/episode/track selection of its own — a
        // release-search result is the whole download, not one file picked out of it
        // — but it DOES now carry which catalog it was found on, so the resolver's
        // per-file narrowing gets the same request shape (TvRequest, ComicRequest, ...)
        // the search side used, not a media-type-less catch-all. An id encoded before
        // this carried a media type (or one from a source that can't map its protocol
        // string) decodes with MediaType null, and falls back to exactly the old
        // behaviour: a bare GeneralRequest is still enough context for the resolver's
        // per-file logic to do no harm.
        var request = decoded.MediaType is { } type
            ? BuildRequest(type, decoded.Release.Title)
            : new GeneralRequest { Title = decoded.Release.Title, Kind = MediaType.Other };
        return await _resolver.ResolveAsync(decoded.Release, request, index, ct);
    }

    private CatalogDescriptor? FindCatalog(string catalogId) =>
        Catalogs.FirstOrDefault(c => c.Id.Equals(catalogId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the concrete <see cref="MediaRequest"/> for a catalog's media
    /// type. <see cref="MediaTypeNames"/> already did the hard part (protocol string ->
    /// MediaType); this just picks the matching request subclass so the grabber routes
    /// to the right providers and Torznab category.</summary>
    private static MediaRequest BuildRequest(MediaType type, string query) => type switch
    {
        MediaType.Movie => new MovieRequest { Title = query },
        MediaType.Tv => new TvRequest { Title = query },
        MediaType.Music => new MusicRequest { Title = query },
        MediaType.Audiobook => new AudiobookRequest { Title = query },
        MediaType.Book => new BookRequest { Title = query },
        MediaType.Comic => new ComicRequest { Title = query },
        _ => new GeneralRequest { Title = query, Kind = type },
    };

    private static CatalogItem ToItem(TorrentResult result, string mediaType) =>
        new(
            Id: EncodeId(result, mediaType),
            Title: result.Title,
            Subtitle: Describe(result),
            MediaType: mediaType);

    private static string Describe(TorrentResult result)
    {
        var parts = new List<string>();
        if (result.SizeBytes is { } size) parts.Add(FormatSize(size));
        if (result.Seeders is { } seeders) parts.Add($"{seeders} seeders");
        parts.Add(result.ProviderName);
        return string.Join(" · ", parts);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0.#} KB",
    };

    /// <summary>The bit of a <see cref="TorrentResult"/> that actually needs to survive
    /// the round trip to the client and back, as JSON. A separate, deliberately
    /// permissive DTO (nothing "required") rather than serializing TorrentResult
    /// itself — a client-supplied id must be free to fail to deserialize (missing
    /// fields, wrong types) without System.Text.Json's `required`-member enforcement
    /// turning that into a thrown exception before this class gets a chance to
    /// validate it and return null instead.</summary>
    private sealed class ReleaseRecord
    {
        [JsonPropertyName("t")] public string? Title { get; set; }
        [JsonPropertyName("p")] public string? Provider { get; set; }
        [JsonPropertyName("h")] public string? InfoHash { get; set; }
        [JsonPropertyName("m")] public string? Magnet { get; set; }
        [JsonPropertyName("d")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("s")] public long? SizeBytes { get; set; }
        [JsonPropertyName("sd")] public int? Seeders { get; set; }

        /// <summary>The catalog's addon-protocol media type string (e.g. "series"),
        /// so <see cref="ResolveAsync"/> can rebuild the same concrete
        /// <see cref="MediaRequest"/> subclass the search side used, instead of a
        /// media-type-less <see cref="GeneralRequest"/> that can't tell a season pack
        /// from an album. Absent on ids encoded before this field existed; decoding
        /// treats that the same as an unrecognized value — MediaType comes back null.</summary>
        [JsonPropertyName("mt")] public string? MediaType { get; set; }
    }

    /// <summary>A release decoded from a client-supplied id, plus the media type it
    /// was searched under (null for a pre-existing id, or one whose protocol string
    /// <see cref="MediaTypeNames"/> no longer recognizes).</summary>
    private readonly record struct DecodedRelease(TorrentResult Release, MediaType? MediaType);

    private static string EncodeId(TorrentResult result, string mediaType)
    {
        var record = new ReleaseRecord
        {
            Title = result.Title,
            Provider = result.ProviderName,
            InfoHash = result.InfoHash,
            Magnet = result.MagnetUri?.ToString(),
            // A DownloadUrl often embeds the indexer manager's own API key and
            // hostname (Prowlarr/Jackett enclosure URLs do) — encoding it into every
            // item id would leak that secret into the client and into every
            // request-path log line (Program.cs only redacts the access token).
            // MagnetResolver (see EverythingBox.Server.Core.Debrid.MagnetResolver)
            // only ever falls back to DownloadUrl when both MagnetUri and InfoHash
            // are absent, so it's safe — and strictly smaller — to omit it here
            // whenever either of those is present.
            DownloadUrl = NeedsDownloadUrl(result) ? result.DownloadUrl?.ToString() : null,
            SizeBytes = result.SizeBytes,
            Seeders = result.Seeders,
            MediaType = mediaType,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(record);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool NeedsDownloadUrl(TorrentResult result) =>
        result.MagnetUri is null && string.IsNullOrWhiteSpace(result.InfoHash);

    /// <summary>Decodes a client-supplied id back into a <see cref="DecodedRelease"/>.
    /// An id arrives from the client and is never trusted: malformed base64, truncated
    /// input, valid-base64-but-non-JSON content, or JSON of the wrong shape all return
    /// null here — none of them throw.</summary>
    private static DecodedRelease? DecodeRelease(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return null;

        byte[] bytes;
        try
        {
            var padded = id.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }

        ReleaseRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<ReleaseRecord>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null || string.IsNullOrEmpty(record.Title) || string.IsNullOrEmpty(record.Provider))
            return null;

        var release = new TorrentResult
        {
            Title = record.Title,
            ProviderName = record.Provider,
            InfoHash = record.InfoHash,
            MagnetUri = TryParseUri(record.Magnet),
            DownloadUrl = TryParseUri(record.DownloadUrl),
            SizeBytes = record.SizeBytes,
            Seeders = record.Seeders,
        };

        var mediaType = MediaTypeNames.TryParseProtocol(record.MediaType, out var type) ? type : (MediaType?)null;
        return new DecodedRelease(release, mediaType);
    }

    private static Uri? TryParseUri(string? value) =>
        !string.IsNullOrEmpty(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
