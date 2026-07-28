using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.AspNetCore.Http.Features;

namespace EverythingBox.Server;

public static class AddonEndpoints
{
    public static void MapBrowse(this WebApplication app, string prefix)
    {
        app.MapGet($"{prefix}/manifest.json", (ManifestBuilder builder, ServerConfig config, SourceRouter router) =>
            Results.Json(builder.Build(config.Manifest.ToOptions(), router.Sources)));

        app.MapGet($"{prefix}/catalog/{{catalogId}}.json",
            (string catalogId, SourceRouter router, CancellationToken ct) =>
                CatalogAsync(catalogId, null, router, ct));

        // The extras segment is e.g. "search=batman&page=2".
        app.MapGet($"{prefix}/catalog/{{catalogId}}/{{extra}}.json",
            (string catalogId, string extra, HttpContext http, SourceRouter router, CancellationToken ct) =>
                CatalogAsync(catalogId, ParseSearch(extra, http), router, ct));

        app.MapGet($"{prefix}/detail/{{type}}/{{id}}.json",
            async (string type, string id, SourceRouter router, CancellationToken ct) =>
            {
                if (!router.TryResolve(id, out var source, out var payload))
                    return Results.Json(Empty());

                var catalog = await source.DetailAsync(payload, new SourceContext(), ct);
                return Results.Json(ToWire(catalog, source.Key));
            });

        // The sources here carry no rich metadata; a valid-but-blank panel is correct.
        app.MapGet($"{prefix}/meta/{{type}}/{{id}}.json", (string type, string id) => Results.Json(new { }));
    }

    private static async Task<IResult> CatalogAsync(
        string catalogId, string? query, SourceRouter router, CancellationToken ct)
    {
        if (!router.TryResolve(catalogId, out var source, out var payload))
            return Results.Json(Empty());

        var catalog = await source.SearchAsync(payload, query, new SourceContext(), ct);
        return Results.Json(ToWire(catalog, source.Key));
    }

    /// <summary>
    /// Reads "search=..." from the RAW request target. ASP.NET decodes the {extra} route
    /// value, which would turn an encoded '&' inside a title into a parameter separator
    /// and truncate the query.
    /// </summary>
    private static string? ParseSearch(string extra, HttpContext http) =>
        ParseSearchCore(extra, http.Features.Get<IHttpRequestFeature>()?.RawTarget);

    /// <summary>
    /// Pure core of <see cref="ParseSearch"/>, split out so it can be unit-tested without
    /// a real request pipeline. Prefers <paramref name="rawTarget"/> (the wire-exact request
    /// target, which still has percent-encoding intact) over <paramref name="extra"/> (the
    /// already-decoded route value) when available.
    /// </summary>
    internal static string? ParseSearchCore(string extra, string? rawTarget)
    {
        var raw = extra;
        if (rawTarget is { Length: > 0 } target)
        {
            var segment = target;

            var question = segment.IndexOf('?');
            if (question >= 0) segment = segment[..question];

            var slash = segment.LastIndexOf('/');
            if (slash >= 0) segment = segment[(slash + 1)..];

            if (segment.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) segment = segment[..^5];
            if (segment.Length > 0) raw = segment;
        }

        foreach (var part in raw.Split('&'))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part[..equals].Equals("search", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(part[(equals + 1)..]);
        }
        return null;
    }

    private static object Empty() => new { title = "", hasMore = false, items = Array.Empty<object>() };

    /// <summary>Prefixes every item id with its owner on the way out, so whatever the
    /// client sends back routes home.</summary>
    private static object ToWire(SourceCatalog catalog, string sourceKey) => new
    {
        title = catalog.Title,
        hasMore = catalog.HasMore,
        items = catalog.Items.Select(i => new
        {
            id = SourceRouter.Prefix(sourceKey, i.Id),
            title = i.Title,
            subtitle = i.Subtitle,
            type = i.MediaType,
            thumbnailUrl = i.ThumbnailUrl,
            expandable = i.Expandable,
        }).ToArray(),
    };
}
