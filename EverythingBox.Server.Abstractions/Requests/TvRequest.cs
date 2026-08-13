namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A request for TV content. The combination of fields selects the granularity:
/// a single episode (Season + Episode), a full season (Season + FullSeason), or
/// the whole series (neither set).
/// </summary>
public sealed class TvRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Tv;

    /// <summary>Season number. Null means "any / full series".</summary>
    public int? Season { get; init; }

    /// <summary>Episode number within the season.</summary>
    public int? Episode { get; init; }

    /// <summary>Absolute episode number, as used by many anime releases.</summary>
    public int? AbsoluteEpisode { get; init; }

    /// <summary>Prefer a single season pack rather than an individual episode.</summary>
    public bool FullSeason { get; init; }

    public override MediaRequest WithTitle(string title) => new TvRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles, PreferredLanguage = PreferredLanguage,
      Season = Season, Episode = Episode, AbsoluteEpisode = AbsoluteEpisode, FullSeason = FullSeason };
}
