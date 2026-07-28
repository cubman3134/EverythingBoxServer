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
    /// decides whether to keep or drop the source's contribution.
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
                    .Select(c => (object)new { id = SourceRouter.Prefix(key, c.Id), name = c.Name, type = c.MediaType })
                    .ToList();
                sourceMediaTypes = source.MediaTypes.ToList();
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Source '{Source}' threw while building the manifest — omitting it. Every other source is unaffected.",
                    SafeLabel(source));
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

    /// <summary>Key itself is plugin-authored and can throw too — fall back to the runtime
    /// type so the log line still identifies which source misbehaved.</summary>
    private static string SafeLabel(IMediaSource source)
    {
        try { return source.Key; }
        catch { return source.GetType().FullName ?? "<unknown source>"; }
    }
}
