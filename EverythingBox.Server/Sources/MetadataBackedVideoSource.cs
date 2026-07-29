using System.Text.Json;
using System.Text.Json.Serialization;
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Sources;

/// <summary>
/// Turns every registered <see cref="IMetadataSource"/> into browsable movie and
/// series shelves — the "browse, then play" flow, as opposed to <see cref="IndexerSearchSource"/>'s
/// search-only shelves.
/// <para>
/// This is an ordinary <see cref="IMediaSource"/> — <c>SourceRouter</c> reaches it the
/// same way it reaches every other source, by splitting "meta:{payload}" on the colon.
/// There is no special routing here or anywhere else.
/// </para>
/// </summary>
public sealed class MetadataBackedVideoSource : IMediaSource
{
    private readonly IReadOnlyList<IMetadataSource> _metadata;
    private readonly ITorrentGrabber _grabber;
    private readonly ReleaseStreamResolver _resolver;
    private readonly ILogger<MetadataBackedVideoSource> _logger;

    public MetadataBackedVideoSource(
        IReadOnlyList<IMetadataSource> metadata,
        ITorrentGrabber grabber,
        ReleaseStreamResolver resolver,
        ILogger<MetadataBackedVideoSource> logger)
    {
        _metadata = metadata;
        _grabber = grabber;
        _resolver = resolver;
        _logger = logger;

        // A shelf is only worth declaring if something can actually fill it — a manifest
        // must not promise a catalog nothing can answer. With no metadata source
        // registered at all, this declares nothing.
        var catalogs = new List<CatalogDescriptor>();
        if (_metadata.Any(source => Supports(source, "movie")))
            catalogs.Add(new CatalogDescriptor("movies", "Movies", "movie"));
        if (_metadata.Any(source => Supports(source, "series")))
            catalogs.Add(new CatalogDescriptor("series", "Series", "series"));
        Catalogs = catalogs;
    }

    public string Key => "meta";

    public IReadOnlyList<CatalogDescriptor> Catalogs { get; }

    // movie/series are built into the client and must NOT be declared here — declaring
    // them would confuse the client. Declared explicitly (rather than relying on
    // IMediaSource's default) because a default interface member is only reachable
    // through the interface, not through a variable typed as the concrete class.
    public IReadOnlyList<MediaTypeDescriptor> MediaTypes { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => SearchAsyncCore(catalogId, query, ct);

    private async Task<SourceCatalog> SearchAsyncCore(string catalogId, string? query, CancellationToken ct)
    {
        var descriptor = FindCatalog(catalogId);
        if (descriptor is null)
            return SourceCatalog.Empty("Browse");

        var items = new List<CatalogItem>();
        foreach (var source in _metadata)
        {
            if (!Supports(source, descriptor.MediaType))
                continue;

            IReadOnlyList<MetadataItem> results;
            try
            {
                results = await source.BrowseAsync(descriptor.MediaType, query, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One misbehaving metadata source must degrade to "skip it", never take
                // the whole shelf down — the others still get to answer.
                _logger.LogWarning(ex,
                    "Metadata source failed to browse '{MediaType}'; skipping it.", descriptor.MediaType);
                continue;
            }

            items.AddRange(results.Select(item => ToCatalogItem(item, descriptor.MediaType)));
        }

        return new SourceCatalog(descriptor.Name, items);
    }

    /// <summary>Expanding a series into episodes is Task 4. For now nothing expands.</summary>
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("Details"));

    public async Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (DecodeItem(itemId) is not { } decoded)
            return null;

        // A series id isn't directly resolvable — it has no single release to grab.
        // Expanding it into episodes (each of which IS resolvable) is Task 4.
        if (decoded.MediaType != MediaType.Movie)
            return null;

        var request = new MovieRequest { Title = decoded.Title, Year = decoded.Year };

        IReadOnlyList<TorrentResult> candidates;
        try
        {
            // SearchRankedAsync, not SearchAsync: browse-then-play wants best-first
            // candidates so `index` walks a sensible order, and only the ranked path
            // applies Ranking config at all.
            candidates = await _grabber.SearchRankedAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Ranked search for '{Title}' failed; nothing to resolve.", decoded.Title);
            return null;
        }

        // `index` selects the N-th best candidate, so a user who rejects a result gets
        // the next one without a re-search — same contract every IMediaSource.ResolveAsync
        // documents.
        if (index < 0 || index >= candidates.Count)
            return null;

        return await _resolver.ResolveAsync(candidates[index], request, 0, ct);
    }

    private CatalogDescriptor? FindCatalog(string catalogId) =>
        Catalogs.FirstOrDefault(c => c.Id.Equals(catalogId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="source"/> supports <paramref name="mediaType"/>.
    /// Guarded: <see cref="IMetadataSource.SupportedMediaTypes"/> is plugin-authored code
    /// and can throw — a bad plugin must not take a catalog (or the whole manifest, if
    /// this ran from the constructor) down with it.</summary>
    private bool Supports(IMetadataSource source, string mediaType)
    {
        try
        {
            return source.SupportedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "A metadata source failed reporting its supported media types; treating it as unsupported for '{MediaType}'.",
                mediaType);
            return false;
        }
    }

    private static CatalogItem ToCatalogItem(MetadataItem item, string mediaType) =>
        new(
            Id: EncodeId(item, mediaType),
            Title: item.Title,
            Subtitle: item.Year?.ToString() ?? "",
            MediaType: mediaType,
            ThumbnailUrl: item.PosterUrl,
            // A series is expandable into episodes (Task 4); a movie is not — it's
            // already the whole thing.
            Expandable: string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase));

    /// <summary>The bit of a <see cref="MetadataItem"/> that needs to survive the round
    /// trip to the client and back, as JSON. A separate, deliberately permissive DTO
    /// (nothing "required") rather than serializing MetadataItem itself — a
    /// client-supplied id must be free to fail to deserialize (missing fields, wrong
    /// types) without System.Text.Json's `required`-member enforcement turning that into
    /// a thrown exception before this class gets a chance to validate it and return null
    /// instead.</summary>
    private sealed class ItemRecord
    {
        [JsonPropertyName("t")] public string? Title { get; set; }
        [JsonPropertyName("y")] public int? Year { get; set; }

        /// <summary>The addon-protocol media-type string (e.g. "series"), so
        /// <see cref="ResolveAsync"/> knows what it's holding without re-deriving it
        /// from the catalog. Absent or unrecognized decodes with no <see cref="DecodedItem"/>
        /// at all — see <see cref="DecodeItem"/>.</summary>
        [JsonPropertyName("mt")] public string? MediaType { get; set; }
    }

    private readonly record struct DecodedItem(string Title, int? Year, MediaType MediaType);

    private static string EncodeId(MetadataItem item, string mediaType)
    {
        var record = new ItemRecord
        {
            Title = item.Title,
            Year = item.Year,
            MediaType = mediaType,
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(record);
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes a client-supplied id. An id arrives from the client and is never
    /// trusted: malformed base64, truncated input, valid-base64-but-non-JSON content, or
    /// JSON of the wrong shape all return null here — none of them throw.</summary>
    private static DecodedItem? DecodeItem(string? id)
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

        ItemRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<ItemRecord>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null || string.IsNullOrEmpty(record.Title))
            return null;

        if (!MediaTypeNames.TryParseProtocol(record.MediaType, out var mediaType))
            return null;

        return new DecodedItem(record.Title, record.Year, mediaType);
    }
}
