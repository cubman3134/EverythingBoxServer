using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

/// <summary>The romhacks surface: what hacks exist for a retro game, and the patch behind one.
///
/// Both endpoints fan out across every registered source and are best-effort. A source that is
/// unreachable, rate-limited or simply has nothing contributes no rows; it never fails the request.
/// "No hacks for this game" and "that source is down" look the same to someone browsing, and neither
/// is worth breaking a page over — so the list endpoint answers 200 with an empty array rather than
/// an error the client would have to render as a dead end.
///
/// The server holds no opinion about where hacks come from. Ids are opaque keys minted by whichever
/// plugin produced them, and the fetch is offered to each source in turn until one claims the id.</summary>
public static class RomhackEndpoints
{
    public static void MapRomhacks(this WebApplication app, string prefix)
    {
        // GET {prefix}/romhacks/{systemId}?title=...
        app.MapGet($"{prefix}/romhacks/{{systemId}}",
            async (string systemId, string? title, IReadOnlyList<IRomhackSource> sources,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Romhacks");
                var rows = new List<RomhackInfo>();

                foreach (var source in sources)
                {
                    try
                    {
                        rows.AddRange(await source.ListAsync(systemId, title ?? "", ct).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;                      // the caller went away; stop, do not swallow
                    }
                    catch (Exception ex)
                    {
                        // One misbehaving source must not cost the others their rows.
                        log.LogWarning(ex, "A romhack source failed listing {System}/{Title}", systemId, title);
                    }
                }

                return Results.Json(rows);
            });

        // GET {prefix}/romhack/{id} — id is opaque and may contain ':' and '/', so it is a catch-all.
        app.MapGet($"{prefix}/romhack/{{*id}}",
            async (string id, IReadOnlyList<IRomhackSource> sources, RomhackStaging staging,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Romhacks");

                // Sweep here, where files are about to be created, rather than on a timer. This call
                // site is self-limiting — it runs only when the feature is used — needs no background
                // service, and cleans at exactly the moment new files arrive. The consequence, worth
                // stating rather than hiding: if nobody fetches, nothing is swept. Nothing is being
                // created either, so the cost is only that a previous session's files linger until
                // the next fetch.
                //
                // Housekeeping never fails a request. A staging root that cannot be read costs a
                // sweep, not the patch set the caller asked for.
                try
                {
                    var removed = staging.Sweep(DateTimeOffset.UtcNow);
                    if (removed > 0) log.LogInformation("Swept {Count} expired romhack staging directories", removed);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Sweeping the romhack staging root failed; continuing with the fetch");
                }

                foreach (var source in sources)
                {
                    try
                    {
                        // A fresh directory per source, not per request: two sources shipping a patch
                        // under the same name would otherwise land on the same path, and the second
                        // would silently rewrite bytes the first had already minted a url for. A
                        // source that does not own the id leaves its directory empty, which the sweep
                        // takes in its own time.
                        var set = await source.FetchAsync(id, staging.NewFetchDirectory(), ct)
                                              .ConfigureAwait(false);
                        if (set is not null) return Results.Json(set);   // first source to claim it wins
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "A romhack source failed fetching {Id}", id);
                    }
                }

                // Nothing claimed it. This one IS a 404: the client asked for a specific hack by id and
                // there is no patch to hand back, which is different from a game simply having no hacks.
                return Results.NotFound();
            });

        // GET {prefix}/romhack-file/{id} — the file a patch set's url points at. The id is the
        // base64url encoding SafeLocalFileServer mints, which never contains '/', so one segment
        // holds it.
        app.MapGet($"{prefix}/romhack-file/{{id}}",
            async (string id, HttpContext http, RomhackStaging staging, ILoggerFactory loggers,
                   CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Romhacks");

                // Every id here arrives from a client, so it is never trusted on its own. The decode,
                // the real-resolve through junctions and symlinks, the containment check against the
                // root and the Range handling are all SafeLocalFileServer's — the same implementation
                // four plugins already serve files through. A second path check here is the failure
                // this route is written to avoid: two containment implementations drift, and only one
                // of them gets the next fix. The instance is per-request because its roots are fixed
                // at construction and it costs nothing to build; the staging root itself is the
                // singleton.
                var files = new SafeLocalFileServer([staging.Root], _ => "application/octet-stream");

                var range = http.Request.Headers.Range.ToString();

                ProxyResponse? file;
                try
                {
                    file = await files.OpenAsync(id, string.IsNullOrEmpty(range) ? null : range, ct)
                                      .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // A delete racing the open surfaces as I/O rather than null. Same answer either
                    // way: the file is not there to serve.
                    log.LogWarning(ex, "romhack file: opening a staged file failed — returning 404");
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Null is the ordinary case, not an incident: an id that does not decode, a path
                // outside the staging root, or — reachable by design, since retention is age-based —
                // a file the sweep already took. All of them are a clean 404. Never a 200 with an
                // empty body: that would install as a zero-byte rom.
                if (file is null)
                {
                    http.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                try
                {
                    try
                    {
                        http.Response.StatusCode = file.StatusCode;
                        http.Response.ContentType = file.ContentType;
                        if (file.ContentLength is { } length) http.Response.ContentLength = length;
                        if (file.AcceptRanges is { } accept) http.Response.Headers.AcceptRanges = accept;
                        if (file.ContentRange is { } contentRange) http.Response.Headers.ContentRange = contentRange;

                        await file.Body.CopyToAsync(http.Response.Body, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        // HasStarted is what decides what is still possible, exactly as in the addon
                        // proxy: before the first byte the headers have not gone out, so this degrades
                        // to a 404 like every other "cannot serve this" case. After the first byte
                        // nothing can turn it into a clean status any more, and a silently truncated
                        // rom is worse than a visible cutoff — so abort the connection instead.
                        if (!http.Response.HasStarted)
                        {
                            log.LogWarning(ex, "romhack file: failed before any bytes were sent — returning 404");
                            http.Response.StatusCode = StatusCodes.Status404NotFound;
                            http.Response.ContentType = null;
                            http.Response.ContentLength = null;
                            http.Response.Headers.Remove("Accept-Ranges");
                            http.Response.Headers.Remove("Content-Range");
                        }
                        else
                        {
                            log.LogWarning(ex, "romhack file: failed after the response had started — aborting");
                            http.Abort();
                        }
                    }
                }
                finally
                {
                    // The body is a FileStream, and it holds a handle on a staged file the sweep will
                    // want to delete. Release it on every exit path — success, mid-copy failure, and
                    // client disconnect alike — or the sweep skips that directory for as long as the
                    // handle lives.
                    try
                    {
                        await file.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "romhack file: disposing the staged file's stream failed");
                    }
                }
            });
    }
}
