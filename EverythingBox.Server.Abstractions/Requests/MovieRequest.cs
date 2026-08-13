namespace EverythingBox.Server.Abstractions;

/// <summary>A request for a single movie.</summary>
public sealed class MovieRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Movie;

    /// <summary>Preferred edition, e.g. "Director's Cut", "Extended", "IMAX".</summary>
    public string? Edition { get; init; }

    public override MediaRequest WithTitle(string title) => new MovieRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles, PreferredLanguage = PreferredLanguage, Edition = Edition };
}
