namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A request for music. <see cref="MediaRequest.Title"/> should hold the most
/// useful primary search term (usually the album); the dedicated fields let
/// providers build more precise queries when supported.
/// </summary>
public sealed class MusicRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Music;

    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>A single track, when you want one song rather than a release.</summary>
    public string? Track { get; init; }

    public override MediaRequest WithTitle(string title) => new MusicRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Artist = Artist, Album = Album, Track = Track };
}
