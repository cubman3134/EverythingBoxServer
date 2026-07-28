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
            (string catalogId, SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
                CatalogAsync(catalogId, null, router, loggers, ct));

        // The extras segment is e.g. "search=batman&page=2".
        app.MapGet($"{prefix}/catalog/{{catalogId}}/{{extra}}.json",
            (string catalogId, string extra, HttpContext http, SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
                CatalogAsync(catalogId, ParseSearch(extra, http), router, loggers, ct));

        app.MapGet($"{prefix}/detail/{{type}}/{{id}}.json",
            async (string type, string id, SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (!router.TryResolve(id, out var source, out var payload))
                    return Results.Json(Empty());

                // ToWire (and the source.Key it prefixes ids with) MUST stay inside this try:
                // both enumerate/read plugin-authored data (catalog.Items, Key) that can throw
                // on its own even when DetailAsync itself succeeded.
                try
                {
                    var catalog = await source.DetailAsync(payload, new SourceContext(), ct);
                    return Results.Json(ToWire(catalog, source.Key));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggers.CreateLogger("Detail").LogError(ex,
                        "detail {Type}/{Id}: source '{Source}' threw — returning empty", type, id, PluginDiagnostics.SafeLabel(source));
                    return Results.Json(Empty());
                }
            });

        // The sources here carry no rich metadata; a valid-but-blank panel is correct.
        app.MapGet($"{prefix}/meta/{{type}}/{{id}}.json", (string type, string id) => Results.Json(new { }));
    }

    private static async Task<IResult> CatalogAsync(
        string catalogId, string? query, SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!router.TryResolve(catalogId, out var source, out var payload))
            return Results.Json(Empty());

        // ToWire (and the source.Key it prefixes ids with) MUST stay inside this try: both
        // enumerate/read plugin-authored data (catalog.Items, Key) that can throw on its own
        // even when SearchAsync itself succeeded.
        try
        {
            var catalog = await source.SearchAsync(payload, query, new SourceContext(), ct);
            return Results.Json(ToWire(catalog, source.Key));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("Catalog").LogError(ex,
                "catalog {CatalogId}: source '{Source}' threw — returning empty catalog", catalogId, PluginDiagnostics.SafeLabel(source));
            return Results.Json(Empty());
        }
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

    public static void MapStreams(this WebApplication app, string prefix)
    {
        // ?n=K asks for the K-th best source, so a user can reject one and get another.
        // ?dl=curl says the client can fetch a URL itself.
        app.MapGet($"{prefix}/stream/{{type}}/{{id}}.json",
            async (string type, string id, int? n, string? dl,
                   SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Stream");

                if (!router.TryResolve(id, out var source, out var payload))
                {
                    log.LogWarning("stream {Type}/{Id}: no source owns this id", type, id);
                    return Results.Json(NoStreams());
                }

                var context = new SourceContext { ClientCanCurl = dl == "curl" };

                SourceStream? stream;
                try
                {
                    stream = await source.ResolveAsync(payload, n ?? 0, context, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "stream {Type}/{Id}: source '{Source}' threw during ResolveAsync — returning no streams",
                        type, id, PluginDiagnostics.SafeLabel(source));
                    return Results.Json(NoStreams());
                }

                if (stream is null) return Results.Json(NoStreams());

                // A notice with no URL: something is in progress. The client shows the
                // message in place of a bare "no source".
                if (string.IsNullOrEmpty(stream.Url))
                    return Results.Json(new { streams = Array.Empty<object>(), notice = stream.Notice });

                if (!SafeUrlGuard.IsClientSafe(stream.Url))
                {
                    log.LogWarning("stream {Type}/{Id}: source '{Source}' returned a url the client cannot play — refusing",
                        type, id, PluginDiagnostics.SafeLabel(source));
                    return Results.Json(NoStreams());
                }

                return stream.Curl
                    ? Results.Json(new { url = stream.Url, mime = stream.Mime, curl = true })
                    : Results.Json(new { url = stream.Url, mime = stream.Mime });
            });

        // Relays bytes for a host the client cannot fetch itself. {name} carries the real
        // filename so the client sees the extension; only {id} is load-bearing.
        app.MapGet($"{prefix}/proxy/{{sourceKey}}/{{id}}/{{name}}",
            async (string sourceKey, string id, string name, HttpContext http,
                   SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
            {
                if (!router.TryResolve(SourceRouter.Prefix(sourceKey, id), out var source, out var payload))
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var range = http.Request.Headers.Range.ToString();

                ProxyResponse? upstream;
                try
                {
                    upstream = await source.OpenAsync(payload, string.IsNullOrEmpty(range) ? null : range, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggers.CreateLogger("Proxy").LogError(ex,
                        "proxy {SourceKey}/{Id}: source '{Source}' threw during OpenAsync — returning 404", sourceKey, id, PluginDiagnostics.SafeLabel(source));
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (upstream is null)
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Whatever a source hands us here (LocalFolderSource.OpenAsync returns a
                // File.OpenRead stream, for one) must be released on every exit path: the
                // success path, an exception mid-copy, and a client disconnect. Without this
                // finally, every /proxy/... request leaked a file handle/lock until finalization.
                try
                {
                    http.Response.StatusCode = upstream.StatusCode;
                    http.Response.ContentType = upstream.ContentType;
                    if (upstream.ContentLength is { } length) http.Response.ContentLength = length;
                    if (upstream.AcceptRanges is { } accept) http.Response.Headers.AcceptRanges = accept;
                    if (upstream.ContentRange is { } contentRange) http.Response.Headers.ContentRange = contentRange;

                    await upstream.Body.CopyToAsync(http.Response.Body, ct);
                }
                finally
                {
                    await upstream.DisposeAsync();
                }
            });
    }

    /// <summary>Serves a file this server built. Range processing is enabled so the
    /// client can seek and resume.</summary>
    public static void MapFiles(this WebApplication app, string prefix)
    {
        app.MapGet($"{prefix}/files/{{name}}", (string name, FileCache cache) =>
        {
            if (name != Path.GetFileName(name)) return Results.NotFound();

            var path = Path.Combine(cache.Root, name);
            return File.Exists(path)
                ? Results.File(path, contentType: "application/octet-stream", enableRangeProcessing: true)
                : Results.NotFound();
        });
    }

    private static object NoStreams() => new { streams = Array.Empty<object>() };

    private static object Empty() => new { title = "", hasMore = false, items = Array.Empty<object>() };

    /// <summary>Prefixes every item id with its owner on the way out, so whatever the
    /// client sends back routes home. A plugin-returned null catalog, a catalog with a
    /// null Items list (e.g. `new SourceCatalog("t", null!)`), or a null ELEMENT inside
    /// an otherwise-real Items list — the likelier plugin mistake — is "nothing found"
    /// (the element is simply skipped) — same as the router finding no owning source —
    /// not a crash. Callers MUST call this from inside their per-source try/catch: the
    /// enumeration below (Items itself, or a throwing GetEnumerator on a custom list) is
    /// plugin-authored code and can throw independently of whatever produced the catalog.</summary>
    private static object ToWire(SourceCatalog? catalog, string sourceKey)
    {
        if (catalog is null) return Empty();

        var items = catalog.Items ?? [];
        return new
        {
            title = catalog.Title,
            hasMore = catalog.HasMore,
            items = items
                .Where(i => i is not null)
                .Select(i => new
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
}
