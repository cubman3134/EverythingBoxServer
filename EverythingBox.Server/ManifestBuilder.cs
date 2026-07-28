using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;

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
public sealed class ManifestBuilder
{
    /// <summary>The client knows these natively — declaring them confuses it.</summary>
    private static readonly HashSet<string> BuiltInTypes =
        new(StringComparer.OrdinalIgnoreCase) { "movie", "series" };

    public object Build(ManifestOptions options, IEnumerable<IMediaSource> sources)
    {
        var ordered = sources.ToList();

        var catalogs = ordered
            .SelectMany(s => s.Catalogs.Select(c => new
            {
                id = SourceRouter.Prefix(s.Key, c.Id),
                name = c.Name,
                type = c.MediaType,
            }))
            .ToArray();

        var mediaTypes = ordered
            .SelectMany(s => s.MediaTypes)
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
            mediaTypes,
            catalogs,
        };
    }
}
