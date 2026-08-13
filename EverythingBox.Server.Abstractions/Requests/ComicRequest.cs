namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A request for a comic book or manga. <see cref="MediaRequest.Title"/> is the
/// series name; volume/issue/chapter pick a specific entry out of a collected
/// release.
/// </summary>
public sealed class ComicRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Comic;

    public string? Author { get; init; }

    /// <summary>Collected-edition or manga volume number.</summary>
    public int? Volume { get; init; }

    /// <summary>Single issue number (floppies).</summary>
    public int? Issue { get; init; }

    /// <summary>Chapter number (manga / webtoons).</summary>
    public int? Chapter { get; init; }

    /// <summary>Preferred file format, e.g. "CBZ", "CBR", "PDF".</summary>
    public string? Format { get; init; }

    public override MediaRequest WithTitle(string title) => new ComicRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles, PreferredLanguage = PreferredLanguage,
      Author = Author, Volume = Volume, Issue = Issue, Chapter = Chapter, Format = Format };
}
