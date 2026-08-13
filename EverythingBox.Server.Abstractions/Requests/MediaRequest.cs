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

    /// <summary>Additional names the same work is indexed under (regional/localised/alternate
    /// titles). The primary <see cref="Title"/> stays the display name; these only widen what
    /// counts as a relevance hit and add search terms in the grabber's fan-out.</summary>
    public IReadOnlyList<string> AlternateTitles { get; init; } = [];

    /// <summary>A caller's per-request preferred content language, as a release-language NAME
    /// (e.g. "Spanish") — folded ahead of the configured Ranking languages when scoring a
    /// release's audio and subtitle languages. Null = no per-request preference (config only).
    /// Produced from an Accept-Language header via <see cref="ContentLanguage"/>.</summary>
    public string? PreferredLanguage { get; init; }

    /// <summary>Return a copy of this request with a different primary <see cref="Title"/> and all
    /// other fields preserved. Used by the grabber to query a provider under an alternate title
    /// without the provider changing. Polymorphic because each subtype carries its own fields.</summary>
    public abstract MediaRequest WithTitle(string title);
}
