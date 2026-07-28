using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Providers;

/// <summary>
/// Builds the free-text search term from a typed request — shared by the direct
/// providers so they phrase queries consistently (title + year for movies,
/// <c>SxxExx</c> for TV, artist/album for music, etc.).
/// </summary>
public static class SearchQuery
{
    public static string Build(MediaRequest request)
    {
        var terms = new List<string>();

        switch (request)
        {
            case TvRequest tv:
                terms.Add(request.Title);
                if (tv.Season is { } s)
                    terms.Add(tv.Episode is { } e ? $"S{s:D2}E{e:D2}" : $"S{s:D2}");
                break;
            case MovieRequest:
                terms.Add(request.Title);
                if (request.Year is { } year)
                    terms.Add(year.ToString());
                break;
            case MusicRequest m:
                // Search by artist + album (the release that's actually on the
                // indexer). The specific track is NOT part of the query — it lives
                // inside the album and is pulled out at the file-selection step.
                AddIf(terms, m.Artist);
                AddIf(terms, m.Album ?? m.Title);
                break;
            case AudiobookRequest a:
                AddIf(terms, a.Author);
                terms.Add(request.Title);
                break;
            case BookRequest b:
                AddIf(terms, b.Author);
                terms.Add(request.Title);
                AddIf(terms, b.Format);
                break;
            case ComicRequest c:
                // Search the series; the specific volume/issue is pulled out at
                // the file-selection step (like a music track in an album).
                AddIf(terms, c.Author);
                terms.Add(request.Title);
                AddIf(terms, c.Format);
                break;
            default:
                terms.Add(request.Title);
                break;
        }

        terms.AddRange(request.AdditionalTerms.Where(t => !string.IsNullOrWhiteSpace(t)));
        return string.Join(' ', terms.Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private static void AddIf(List<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            terms.Add(value.Trim());
    }
}
