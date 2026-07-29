namespace EverythingBox.Server.Abstractions;

/// <summary>One browsable title. <paramref name="Id"/> is the metadata source's own
/// identifier; the host wraps it, so it never reaches the client bare.</summary>
public sealed record MetadataItem(
    string Id,
    string Title,
    string MediaType,
    int? Year = null,
    string? PosterUrl = null,
    string? Overview = null);

public sealed record MetadataEpisode(
    string Id,
    int Season,
    int Episode,
    string Title,
    string? Overview = null);

/// <summary>
/// Supplies titles to browse and episodes to expand. Unlike <see cref="IMediaSource"/>
/// a metadata source is consulted rather than routed to — it never owns an id namespace,
/// so it needs a name for logging rather than a key for dispatch.
/// </summary>
public interface IMetadataSource
{
    string Name { get; }

    /// <summary>Protocol media-type strings this source can browse — "movie", "series".</summary>
    IReadOnlyList<string> SupportedMediaTypes { get; }

    /// <summary>Browse, or search when <paramref name="query"/> is supplied.</summary>
    Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct);

    /// <summary>
    /// Episodes of a series. Defaults to empty so a movie-only source need not write it —
    /// but a source declaring "series" in <see cref="SupportedMediaTypes"/> is expected to
    /// implement it, or its series will expand into nothing.
    /// </summary>
    Task<IReadOnlyList<MetadataEpisode>> EpisodesAsync(string seriesId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataEpisode>>([]);
}
