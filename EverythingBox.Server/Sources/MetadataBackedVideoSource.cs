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

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => DetailAsyncCore(itemId, ct);

    private async Task<SourceCatalog> DetailAsyncCore(string itemId, CancellationToken ct)
    {
        // Only a series id (not a movie, and not an episode id — an episode has
        // nothing further to expand) has episodes to list.
        if (DecodeItem(itemId) is not { } decoded || decoded.MediaType != MediaType.Tv || decoded.Season is not null)
            return SourceCatalog.Empty("Episodes");

        var items = new List<CatalogItem>();
        foreach (var source in _metadata)
        {
            if (!Supports(source, "series"))
                continue;

            IReadOnlyList<MetadataEpisode> episodes;
            try
            {
                episodes = await source.EpisodesAsync(decoded.SourceId ?? decoded.Title, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Same containment discipline as SearchAsyncCore: one misbehaving
                // metadata source degrades to "skip it", never takes the whole
                // expansion down.
                _logger.LogWarning(ex,
                    "Metadata source failed to list episodes for '{Title}'; skipping it.", decoded.Title);
                continue;
            }

            items.AddRange(episodes.Select(episode => ToEpisodeItem(decoded.Title, episode)));
        }

        return new SourceCatalog("Episodes", items);
    }

    public async Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (DecodeItem(itemId) is not { } decoded)
            return null;

        // A bare series id isn't directly resolvable — it has no single release to
        // grab. An episode id (Tv + Season/Episode both present) is; so is a movie.
        MediaRequest? request = decoded.MediaType switch
        {
            MediaType.Movie => new MovieRequest { Title = decoded.Title, Year = decoded.Year },
            MediaType.Tv when decoded.Season is { } season && decoded.Episode is { } episode =>
                new TvRequest { Title = decoded.Title, Season = season, Episode = episode },
            _ => null,
        };
        if (request is null)
            return null;

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

        if (index < 0)
            return null;

        // `index` walks playable FILES first, then falls through to the next
        // candidate release — not releases directly. A release is only as good as
        // the file debrid hands back for it, and a season pack can match the wrong
        // episode; a user stuck on the wrong file needs a way to reach the next one
        // without throwing away the whole release. Resolved lazily, one release at a
        // time: each release costs a debrid round trip, so nothing past the release
        // that satisfies `index` is ever touched.
        var remaining = index;
        foreach (var candidate in candidates)
        {
            var options = await _resolver.ResolveAllAsync(candidate, request, ct);
            if (remaining < options.Count)
                return options[remaining];
            remaining -= options.Count;
        }

        return null;
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
            // A series is expandable into episodes; a movie is not — it's already
            // the whole thing.
            Expandable: string.Equals(mediaType, "series", StringComparison.OrdinalIgnoreCase));

    /// <summary>An episode's id carries the SERIES title (not the episode's own
    /// title) plus season and episode — that's what <see cref="ResolveAsync"/> needs
    /// to build a <see cref="TvRequest"/> that lets <c>MediaFileMatcher</c> pull the
    /// one episode out of a season pack. The media type is deliberately still
    /// "series", not some distinct "episode" string — <see cref="MediaTypeNames"/>
    /// has no mapping for one, and decoding relies on this id resolving to
    /// <see cref="MediaType.Tv"/> the same way a bare series id does.</summary>
    private static CatalogItem ToEpisodeItem(string seriesTitle, MetadataEpisode episode) =>
        new(
            Id: EncodeEpisodeId(seriesTitle, episode),
            Title: $"S{episode.Season}E{episode.Episode} - {episode.Title}",
            Subtitle: episode.Overview ?? "",
            MediaType: "series");

    /// <summary>The bit of a <see cref="MetadataItem"/> that needs to survive the round
    /// trip to the client and back, as JSON. A separate, deliberately permissive DTO
    /// (nothing "required") rather than serializing MetadataItem itself — a
    /// client-supplied id must be free to fail to deserialize (missing fields, wrong
    /// types) without System.Text.Json's `required`-member enforcement turning that into
    /// a thrown exception before this class gets a chance to validate it and return null
    /// instead.</summary>
    private sealed class ItemRecord
    {
        /// <summary>A movie's own title, or an episode's SERIES title — never the
        /// episode's own title. See <see cref="ToEpisodeItem"/>.</summary>
        [JsonPropertyName("t")] public string? Title { get; set; }
        [JsonPropertyName("y")] public int? Year { get; set; }

        /// <summary>The addon-protocol media-type string (e.g. "series"), so
        /// <see cref="ResolveAsync"/> knows what it's holding without re-deriving it
        /// from the catalog. Absent or unrecognized decodes with no <see cref="DecodedItem"/>
        /// at all — see <see cref="DecodeItem"/>.</summary>
        [JsonPropertyName("mt")] public string? MediaType { get; set; }

        /// <summary>The metadata source's own id for the title (<c>MetadataItem.Id</c>),
        /// so <see cref="DetailAsyncCore"/> can ask the source for episodes of THIS
        /// title rather than guessing from the title string alone. Absent on an
        /// episode id — an episode has nothing further to expand.</summary>
        [JsonPropertyName("id")] public string? SourceId { get; set; }

        /// <summary>Season and episode: present together only on an episode id. Their
        /// presence, not the media-type string, is what distinguishes an episode id
        /// from the series id it was expanded from — both carry <c>mt: "series"</c>.</summary>
        [JsonPropertyName("sn")] public int? Season { get; set; }
        [JsonPropertyName("ep")] public int? Episode { get; set; }
    }

    private readonly record struct DecodedItem(string Title, int? Year, MediaType MediaType, string? SourceId, int? Season, int? Episode);

    private static string EncodeId(MetadataItem item, string mediaType) =>
        Encode(new ItemRecord
        {
            Title = item.Title,
            Year = item.Year,
            MediaType = mediaType,
            SourceId = item.Id,
        });

    private static string EncodeEpisodeId(string seriesTitle, MetadataEpisode episode) =>
        Encode(new ItemRecord
        {
            Title = seriesTitle,
            MediaType = "series",
            Season = episode.Season,
            Episode = episode.Episode,
        });

    private static string Encode(ItemRecord record)
    {
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

        return new DecodedItem(record.Title, record.Year, mediaType, record.SourceId, record.Season, record.Episode);
    }
}
