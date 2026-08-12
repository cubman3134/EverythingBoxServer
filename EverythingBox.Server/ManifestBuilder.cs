using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server;

public sealed record ManifestOptions(
    string Id,
    string Name,
    string Version,
    string Description,
    string Accent);

/// <summary>
/// Composes the addon manifest the EverythingBox client consumes. Installing a plugin
/// changes the manifest; the client needs no changes.
/// </summary>
public sealed class ManifestBuilder(ILogger<ManifestBuilder>? log = null)
{
    private readonly ILogger<ManifestBuilder> _log = log ?? NullLogger<ManifestBuilder>.Instance;

    /// <summary>The client knows these natively — declaring them confuses it.</summary>
    private static readonly HashSet<string> BuiltInTypes =
        new(StringComparer.OrdinalIgnoreCase) { "movie", "series" };

    /// <summary>
    /// Every member touched below (Key, Catalogs, MediaTypes) is plugin-authored code and can
    /// throw at any time on every request — a source whose Catalogs getter throws must not turn
    /// /manifest.json into a 500 for every OTHER installed source. Each source is read inside its
    /// own try: on failure it is logged and simply omitted from the manifest — everything else
    /// still comes back. Reading Catalogs/MediaTypes into a list INSIDE the try is deliberate:
    /// both are lazy-evaluable, so a throw during enumeration must happen before this method
    /// decides whether to keep or drop the source's contribution. A null ELEMENT in MediaTypes
    /// (as opposed to a null Type string, which the post-loop filter/group-by tolerate fine) is
    /// discarded here too, inside the same try — the combined list is filtered again after the
    /// loop, and a null element would NRE there for every source, not just the one that misbehaved.
    /// A null CatalogDescriptor ELEMENT in Catalogs gets the same treatment, for the same reason.
    ///
    /// The catch below is deliberately bare — no "when (ex is not OperationCanceledException)"
    /// filter. This method takes no CancellationToken: nothing it does can be legitimately
    /// cancelled, so there is no genuine cancellation for such a filter to let through. Its only
    /// effect would be to let a source that merely THROWS an OperationCanceledException (which
    /// any plugin can do, deliberately or by an unrelated internal timeout of its own) escape
    /// containment and 500 /manifest.json for every other installed source — the exact regression
    /// this comment exists to prevent. Do not add that filter "for consistency" with
    /// AddonEndpoints' request-scoped catches; they have a real CancellationToken to test against
    /// and this method does not.
    /// </summary>
    public object Build(ManifestOptions options, IEnumerable<IMediaSource> sources)
    {
        var catalogs = new List<object>();
        var mediaTypes = new List<MediaTypeDescriptor>();

        foreach (var source in sources)
        {
            List<object> sourceCatalogs;
            List<MediaTypeDescriptor> sourceMediaTypes;
            try
            {
                var key = source.Key;
                sourceCatalogs = source.Catalogs
                    .Where(c => c is not null)
                    .Select(c => (object)new { id = SourceRouter.Prefix(key, c.Id), name = c.Name, type = c.Kind })
                    .ToList();
                sourceMediaTypes = source.MediaTypes.Where(t => t is not null).ToList();
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Source '{Source}' threw while building the manifest — omitting it. Every other source is unaffected.",
                    PluginDiagnostics.SafeLabel(source));
                continue;
            }

            catalogs.AddRange(sourceCatalogs);
            mediaTypes.AddRange(sourceMediaTypes);
        }

        var mediaTypesOut = mediaTypes
            .Where(t => !BuiltInTypes.Contains(t.Type))
            .GroupBy(t => t.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(t => new
            {
                type = t.Type,
                color = t.Color,
                icon = t.Icon,
                openKind = t.OpenKind,
                detailLayout = t.DetailLayout,
            })
            .ToArray();

        return new
        {
            id = options.Id,
            version = options.Version,
            name = options.Name,
            type = "media-source",
            description = options.Description,
            accent = options.Accent,
            mediaTypes = mediaTypesOut,
            catalogs = catalogs.ToArray(),
        };
    }

}
