using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Providers.Torznab;

/// <summary>
/// Builds Torznab request URLs from a <see cref="MediaRequest"/>: chooses the
/// search function (<c>tvsearch</c>/<c>movie</c>/<c>music</c>/<c>search</c>),
/// maps media types to Newznab categories, and threads through season/episode
/// and external ids. Pure and side-effect free, so it is easy to unit test.
/// </summary>
public static class TorznabQueryBuilder
{
    /// <summary>The free-text <c>q</c> term for a request.</summary>
    public static string BuildSearchTerm(MediaRequest request)
    {
        var terms = new List<string>();

        switch (request)
        {
            case MusicRequest m:
                AddIf(terms, m.Artist);
                AddIf(terms, m.Album ?? m.Title);
                AddIf(terms, m.Track);
                break;
            case AudiobookRequest a:
                AddIf(terms, a.Author);
                AddIf(terms, a.Title);
                break;
            case MovieRequest:
                AddIf(terms, request.Title);
                if (request.Year is { } year)
                    terms.Add(year.ToString());
                break;
            default: // TV (season/ep are sent as params) and everything else
                AddIf(terms, request.Title);
                break;
        }

        terms.AddRange(request.AdditionalTerms.Where(t => !string.IsNullOrWhiteSpace(t)));
        return string.Join(' ', terms);
    }

    /// <summary>The full request URI for a search term against an endpoint.</summary>
    public static Uri BuildUri(TorznabOptions options, string searchTerm, MediaRequest request)
    {
        var function = request.MediaType switch
        {
            MediaType.Tv => "tvsearch",
            MediaType.Movie => "movie",
            MediaType.Music => "music",
            _ => "search",
        };

        var p = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                p.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        Add("t", function);
        Add("apikey", options.ApiKey);
        Add("q", searchTerm);
        Add("cat", ResolveCategories(options, request.MediaType));

        if (request is TvRequest tv)
        {
            if (tv.Season is { } s) Add("season", s.ToString());
            if (tv.Episode is { } e) Add("ep", e.ToString());
        }

        // External ids give exact matches when the indexer supports them.
        if (request.MediaType is MediaType.Movie or MediaType.Tv
            && request.ExternalIds.TryGetValue("imdb", out var imdb))
            Add("imdbid", imdb.TrimStart('t', 'T'));
        if (request is TvRequest && request.ExternalIds.TryGetValue("tvdb", out var tvdb))
            Add("tvdbid", tvdb);
        if (request is MovieRequest && request.ExternalIds.TryGetValue("tmdb", out var tmdb))
            Add("tmdbid", tmdb);

        if (options.Limit is { } limit)
            Add("limit", limit.ToString());

        var builder = new UriBuilder(options.BaseUrl);
        var existing = builder.Query.TrimStart('?');
        var combined = string.Join('&', p);
        builder.Query = existing.Length > 0 ? $"{existing}&{combined}" : combined;
        return builder.Uri;
    }

    private static string ResolveCategories(TorznabOptions options, MediaType type)
        => options.CategoryOverrides.TryGetValue(type, out var c) ? c : DefaultCategory(type);

    private static string DefaultCategory(MediaType type) => type switch
    {
        MediaType.Movie => "2000",
        MediaType.Tv => "5000",
        MediaType.Music => "3000",
        MediaType.Audiobook => "3030",
        MediaType.Book => "7000",
        MediaType.Comic => "7030",
        _ => string.Empty,
    };

    private static void AddIf(List<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            terms.Add(value.Trim());
    }
}
