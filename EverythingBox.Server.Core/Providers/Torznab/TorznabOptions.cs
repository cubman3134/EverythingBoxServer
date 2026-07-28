using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Providers.Torznab;

/// <summary>
/// Configuration for a single Torznab endpoint — typically a Prowlarr or Jacket
/// indexer URL. For Prowlarr this looks like
/// <c>http://localhost:9696/1/api</c>; for Jackett,
/// <c>http://localhost:9117/api/v2.0/indexers/&lt;id&gt;/results/torznab/</c>.
/// </summary>
public sealed class TorznabOptions
{
    /// <summary>The Torznab API endpoint. Query parameters are appended to this.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>API key, sent as the <c>apikey</c> query parameter when set.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Display name, stamped onto every result.</summary>
    public string Name { get; init; } = "Torznab";

    /// <summary>Optional cap on results requested (the <c>limit</c> parameter).</summary>
    public int? Limit { get; init; }

    /// <summary>
    /// Override the default Newznab category id(s) for a media type, e.g.
    /// <c>{ MediaType.Movie, "2040,2045" }</c> to target HD + UHD only.
    /// </summary>
    public IReadOnlyDictionary<MediaType, string> CategoryOverrides { get; init; }
        = new Dictionary<MediaType, string>();

    /// <summary>Which media types this endpoint should be offered for.</summary>
    public IReadOnlySet<MediaType> SupportedMediaTypes { get; init; } = new HashSet<MediaType>
    {
        MediaType.Movie,
        MediaType.Tv,
        MediaType.Music,
        MediaType.Audiobook,
        MediaType.Book,
        MediaType.Comic,
    };
}
