namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A request for an e-book. Distinct from <see cref="AudiobookRequest"/> (which is
/// audio); this targets text formats like EPUB/PDF/MOBI.
/// </summary>
public sealed class BookRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Book;

    public string? Author { get; init; }

    /// <summary>Preferred file format, e.g. "EPUB", "PDF", "MOBI".</summary>
    public string? Format { get; init; }

    public override MediaRequest WithTitle(string title) => new BookRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Author = Author, Format = Format };
}
