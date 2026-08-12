using EverythingBox.Server.Sync;
using Microsoft.AspNetCore.Http;

namespace EverythingBox.Server;

public static class SyncEndpoints
{
    public static void MapSync(this WebApplication app, string prefix)
    {
        // GET list
        app.MapGet($"{prefix}/sync/{{ns}}", async (string ns, SyncStore store, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var objects = await store.ListAsync(ns, ct);
            return Results.Json(new { objects });
        });

        // GET one object's bytes
        app.MapGet($"{prefix}/sync/{{ns}}/{{**key}}", async (string ns, string key, SyncStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var obj = await store.GetAsync(ns, key, ct);
            if (obj is null) return Results.NotFound();
            http.Response.Headers.ETag = Quote(obj.Version);
            http.Response.Headers["X-Sync-Meta"] = obj.Meta ?? "";
            return Results.File(obj.BlobPath, "application/octet-stream");
        });

        // PUT (conditional) — the first write route on this host
        app.MapPut($"{prefix}/sync/{{ns}}/{{**key}}", async (string ns, string key, SyncStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            // Kestrel's small default request-body limit (~30MB) would 413 a large savestate before the
            // store can count it against MaxObjectBytes (which can be far larger). Remove Kestrel's limit
            // for THIS request only and let the store be the single adjudicator: CopyCappedAsync streams
            // and aborts at MaxObjectBytes (bounded memory + a capped temp file), returning a deterministic
            // 400 TooLarge for any over-cap body — rather than Kestrel's 413 coinciding at the boundary.
            var feat = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (feat is { IsReadOnly: false }) feat.MaxRequestBodySize = null;
            var condition = ParseCondition(http.Request);
            var meta = http.Request.Headers.TryGetValue("X-Sync-Meta", out var m) ? m.ToString() : null;
            var outcome = await store.PutAsync(ns, key, http.Request.Body, condition, meta, ct);
            return ToResult(outcome, http);
        });

        // DELETE (tombstone, conditional)
        app.MapDelete($"{prefix}/sync/{{ns}}/{{**key}}", async (string ns, string key, SyncStore store, HttpContext http, CancellationToken ct) =>
        {
            if (!SyncStore.IsValidNamespace(ns)) return Results.BadRequest();
            var outcome = await store.DeleteAsync(ns, key, ParseCondition(http.Request), ct);
            return ToResult(outcome, http);
        });
    }

    private static string Quote(string v) => "\"" + v + "\"";
    private static string Unquote(string v) => v.Trim().Trim('"');

    private static SyncCondition ParseCondition(HttpRequest req)
    {
        var ifNoneMatch = req.Headers.IfNoneMatch.ToString();
        if (ifNoneMatch.Trim() == "*") return new SyncCondition(SyncConditionKind.IfNoneMatchStar);
        var ifMatch = req.Headers.IfMatch.ToString();
        if (!string.IsNullOrWhiteSpace(ifMatch)) return new SyncCondition(SyncConditionKind.IfMatch, Unquote(ifMatch));
        return SyncCondition.None;
    }

    private static IResult ToResult(SyncWriteOutcome outcome, HttpContext http) => outcome.Status switch
    {
        SyncWriteStatus.Ok => SetETagNoContent(http, outcome.Version!),
        SyncWriteStatus.PreconditionFailed => Results.StatusCode(StatusCodes.Status412PreconditionFailed),
        SyncWriteStatus.QuotaExceeded => Results.StatusCode(StatusCodes.Status507InsufficientStorage),
        SyncWriteStatus.TooLarge => Results.BadRequest(),
        _ => Results.StatusCode(500),
    };

    private static IResult SetETagNoContent(HttpContext http, string version)
    {
        http.Response.Headers.ETag = Quote(version);
        return Results.NoContent();
    }
}
