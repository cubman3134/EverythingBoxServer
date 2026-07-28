namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Base "what do I want" description. Concrete subclasses add fields specific to
/// their <see cref="MediaType"/> (season/episode, artist/album, ...).
/// Providers receive the strongly-typed request and decide how to turn it into a
/// search query for their own backend.
/// </summary>
public abstract class MediaRequest
{
    /// <summary>The media category. Used to route the request to capable providers.</summary>
    public abstract MediaType MediaType { get; }

    /// <summary>
    /// Primary human title of the work — show / movie / album name. This is the
    /// main term providers search on.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>Release year, when known. Helps disambiguate remakes and reissues.</summary>
    public int? Year { get; init; }

    /// <summary>
    /// External identifiers keyed by source, e.g. <c>{ "imdb": "tt0133093",
    /// "tvdb": "78901" }</c>. Providers that support ID-based lookup can use these
    /// for exact matches instead of fuzzy text search.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExternalIds { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Extra free-form terms appended to the search query.</summary>
    public IReadOnlyList<string> AdditionalTerms { get; init; } = [];
}
