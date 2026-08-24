using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

/// <summary>The homebrew surface: what homebrew exists for a retro system.
///
/// One endpoint, deliberately. A row's id is already a media id, so the client plays it through the
/// ordinary stream route — there is no fetch companion here and adding one would only be a second
/// download path to keep correct, for nothing the first one does not already do.
///
/// It fans out across every registered source and is best-effort. A source that is unreachable,
/// rate-limited or simply has nothing contributes no rows; it never fails the request. "No homebrew
/// for this console" and "that source is down" look the same to someone browsing, and neither is
/// worth breaking a page over — so a server with no source at all still answers 200 with an empty
/// array rather than an error the client would have to render as a dead end.</summary>
public static class HomebrewEndpoints
{
    public static void MapHomebrew(this WebApplication app, string prefix)
    {
        // GET {prefix}/homebrew/{systemId}?cursor=...
        app.MapGet($"{prefix}/homebrew/{{systemId}}",
            async (string systemId, string? cursor, IReadOnlyList<IHomebrewSource> sources,
                   ILoggerFactory loggers, CancellationToken ct) =>
            {
                var log = loggers.CreateLogger("Homebrew");
                var rows = new List<HomebrewTitle>();
                string? next = null;

                foreach (var source in sources)
                {
                    try
                    {
                        var page = await source.ListAsync(systemId, cursor, ct).ConfigureAwait(false);
                        rows.AddRange(page.Items);
                        // First source with more to give decides the cursor. With one source — the
                        // normal case — this is simply its own. With several, paging follows whichever
                        // still has pages; the alternative is a compound cursor this server would have
                        // to understand, and it deliberately understands nothing about them.
                        next ??= page.NextCursor;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;                      // the caller went away; stop, do not swallow
                    }
                    catch (Exception ex)
                    {
                        // One misbehaving source must not cost the others their rows.
                        log.LogWarning(ex, "A homebrew source failed listing {System}", systemId);
                    }
                }

                return Results.Json(new HomebrewListing(rows, next));
            });
    }
}
