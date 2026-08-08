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
            (string catalogId, HttpContext http, SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
                CatalogAsync(catalogId, null, http, router, loggers, ct));

        // The extras segment is e.g. "search=batman&page=2".
        app.MapGet($"{prefix}/catalog/{{catalogId}}/{{extra}}.json",
            (string catalogId, string extra, HttpContext http, SourceRouter router, ILoggerFactory loggers, CancellationToken ct) =>
                CatalogAsync(catalogId, ParseSearch(extra, http), http, router, loggers, ct));

        app.MapGet($"{prefix}/detail/{{type}}/{{id}}.json", DetailAsync);

        // The sources here carry no rich metadata; a valid-but-blank panel is correct.
        app.MapGet($"{prefix}/meta/{{type}}/{{id}}.json", (string type, string id) => Results.Json(new { }));
    }

    /// <summary>
    /// The catch below tests whether cancellation was ACTUALLY requested, not just the
    /// exception's type. "when (ex is not OperationCanceledException || !ct.IsCancellationRequested)" alone was a
    /// regression: a plugin can throw OperationCanceledException for reasons that have
    /// nothing to do with this request being cancelled (its own internal timeout, for
    /// instance), and that filter let such a throw escape containment and 500 the request —
    /// exactly the failure every other plugin-authored call in this file is guarded
    /// against. A GENUINE cancellation (ct.IsCancellationRequested is true) still must not
    /// be swallowed into a normal "empty" result — that would hide a real client disconnect
    /// behind a false-looking success — so it is deliberately left to propagate.
    /// </summary>
    internal static async Task<IResult> CatalogAsync(
        string catalogId, string? query, HttpContext http, SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!router.TryResolve(catalogId, out var source, out var payload))
            return Results.Json(Empty());

        // ToWire (and the source.Key it prefixes ids with) MUST stay inside this try: both
        // enumerate/read plugin-authored data (catalog.Items, Key) that can throw on its own
        // even when SearchAsync itself succeeded.
        try
        {
            var catalog = await source.SearchAsync(payload, query, new SourceContext { RequestHeaders = ForwardableHeaders(http) }, ct);
            return Results.Json(ToWire(catalog, source.Key));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            loggers.CreateLogger("Catalog").LogError(ex,
                "catalog {CatalogId}: source '{Source}' threw — returning empty catalog", catalogId, PluginDiagnostics.SafeLabel(source));
            return Results.Json(Empty());
        }
    }

    /// <summary>Same cancellation-vs-exception-type reasoning as <see cref="CatalogAsync"/>.</summary>
    internal static async Task<IResult> DetailAsync(
        string type, string id, SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            loggers.CreateLogger("Detail").LogError(ex,
                "detail {Type}/{Id}: source '{Source}' threw — returning empty", type, id, PluginDiagnostics.SafeLabel(source));
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
        app.MapGet($"{prefix}/stream/{{type}}/{{id}}.json", StreamAsync);

        // Relays bytes for a host the client cannot fetch itself. {name} carries the real
        // filename so the client sees the extension; only {id} is load-bearing.
        app.MapGet($"{prefix}/proxy/{{sourceKey}}/{{id}}/{{name}}", ProxyAsync);
    }

    /// <summary>Same cancellation-vs-exception-type reasoning as <see cref="CatalogAsync"/>.</summary>
    internal static async Task<IResult> StreamAsync(
        string type, string id, int? n, string? dl, HttpContext http,
        SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("Stream");

        if (!router.TryResolve(id, out var source, out var payload))
        {
            log.LogWarning("stream {Type}/{Id}: no source owns this id", type, id);
            return Results.Json(NoStreams());
        }

        var context = new SourceContext { ClientCanCurl = dl == "curl", RequestHeaders = ForwardableHeaders(http) };

        SourceStream? stream;
        try
        {
            stream = await source.ResolveAsync(payload, n ?? 0, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
    }

    /// <summary>
    /// Same cancellation-vs-exception-type reasoning as <see cref="CatalogAsync"/> for the
    /// OpenAsync call. The body-relay half below has its own, larger comment: I1.
    /// </summary>
    internal static async Task ProxyAsync(
        string sourceKey, string id, string name, HttpContext http,
        SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("Proxy");

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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogError(ex,
                "proxy {SourceKey}/{Id}: source '{Source}' threw during OpenAsync — returning 404", sourceKey, id, PluginDiagnostics.SafeLabel(source));
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (upstream is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // I1: everything below — Body itself (can be null despite the constructor
        // parameter's non-nullable annotation; runtime doesn't enforce that), Body.ReadAsync/
        // CopyToAsync, Body.DisposeAsync, Owner.Dispose, and the StatusCode/ContentLength a
        // plugin set on the ProxyResponse — is plugin-authored and can fail independently of
        // whether OpenAsync itself succeeded. HttpResponse.HasStarted is the load-bearing
        // signal for what we can still do about a failure: before the first byte is written,
        // ASP.NET has not sent headers yet, so a failure here degrades exactly like every
        // other "source can't serve this" case (404) — StatusCode/ContentLength are
        // validated BEFORE they ever reach ASP.NET's own (throwing) setters, specifically so
        // an implausible value takes this same clean path instead of an unhandled
        // ArgumentOutOfRangeException. After the first byte, headers are already gone —
        // nothing can turn this into a clean status any more, so the best available
        // behavior is: stop, log, abort the connection so the client sees a clear cutoff
        // rather than a silently truncated file, and never let the exception escape to
        // corrupt what was otherwise a successful relay.
        try
        {
            try
            {
                if (upstream.Body is null)
                    throw new InvalidOperationException(
                        $"Source '{PluginDiagnostics.SafeLabel(source)}' returned a ProxyResponse with a null Body.");

                if (upstream.StatusCode is < 100 or > 599)
                    throw new InvalidOperationException(
                        $"Source '{PluginDiagnostics.SafeLabel(source)}' returned an implausible StatusCode {upstream.StatusCode}.");

                if (upstream.ContentLength is < 0)
                    throw new InvalidOperationException(
                        $"Source '{PluginDiagnostics.SafeLabel(source)}' returned a negative ContentLength {upstream.ContentLength}.");

                http.Response.StatusCode = upstream.StatusCode;
                http.Response.ContentType = upstream.ContentType;
                if (upstream.ContentLength is { } length) http.Response.ContentLength = length;
                if (upstream.AcceptRanges is { } accept) http.Response.Headers.AcceptRanges = accept;
                if (upstream.ContentRange is { } contentRange) http.Response.Headers.ContentRange = contentRange;

                await upstream.Body.CopyToAsync(http.Response.Body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (!http.Response.HasStarted)
                {
                    log.LogError(ex,
                        "proxy {SourceKey}/{Id}: source '{Source}' failed before any bytes were sent — returning 404",
                        sourceKey, id, PluginDiagnostics.SafeLabel(source));
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    http.Response.ContentType = null;
                    http.Response.ContentLength = null;
                    http.Response.Headers.Remove("Accept-Ranges");
                    http.Response.Headers.Remove("Content-Range");
                }
                else
                {
                    log.LogError(ex,
                        "proxy {SourceKey}/{Id}: source '{Source}' failed after the response had already started — aborting the connection",
                        sourceKey, id, PluginDiagnostics.SafeLabel(source));
                    http.Abort();
                }
            }
        }
        finally
        {
            // Whatever a source hands us here (LocalFolderSource.OpenAsync returns a
            // File.OpenRead stream, for one) must be released on every exit path: the
            // success path, an exception mid-copy, and a client disconnect. This alone
            // (try/finally, no catch) used to leak a file handle/lock until finalization.
            // The catch here is the I1 fix: Body.DisposeAsync and Owner.Dispose are just
            // as plugin-authored as everything above and can throw on their own — that
            // must never escape and corrupt a response that otherwise completed cleanly.
            try
            {
                await upstream.DisposeAsync();
            }
            catch (Exception ex)
            {
                log.LogError(ex,
                    "proxy {SourceKey}/{Id}: source '{Source}' threw while disposing its ProxyResponse",
                    sourceKey, id, PluginDiagnostics.SafeLabel(source));
            }
        }
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

    /// <summary>Headers a plugin is never allowed to see: credentials (Authorization, Cookie,
    /// Set-Cookie, Proxy-Authorization) and hop-by-hop headers that are meaningless once
    /// removed from this specific client-to-host connection.</summary>
    private static readonly HashSet<string> BlockedHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Authorization", "Cookie", "Set-Cookie",
            "Proxy-Authorization", "Connection", "Keep-Alive", "Transfer-Encoding", "TE",
            "Trailer", "Upgrade", "Host" };

    /// <summary>Curates the incoming request's headers for a plugin: everything except
    /// <see cref="BlockedHeaders"/>, case-insensitive, or null when nothing is left to forward.</summary>
    private static IReadOnlyDictionary<string, string>? ForwardableHeaders(HttpContext http)
    {
        Dictionary<string, string>? headers = null;
        foreach (var h in http.Request.Headers)
        {
            if (BlockedHeaders.Contains(h.Key)) continue;
            (headers ??= new(StringComparer.OrdinalIgnoreCase))[h.Key] = h.Value.ToString();
        }
        return headers;
    }

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
