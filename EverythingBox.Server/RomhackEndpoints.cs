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
            async (string id, IReadOnlyList<IRomhackSource> sources, ILoggerFactory loggers,
                   CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Romhacks");

                foreach (var source in sources)
                {
                    try
                    {
                        var set = await source.FetchAsync(id, ct).ConfigureAwait(false);
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
    }
}
