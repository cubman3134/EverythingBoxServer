namespace EverythingBox.Server.Abstractions;

/// <summary>One labelled fact row on an item's detail panel (e.g. Year, Runtime).</summary>
public sealed record MetaFact(string Label, string Value);

/// <summary>
/// Rich per-item detail for the meta panel. <see cref="ImageUrl"/> may be a relative
/// <c>proxy/{key}/{id}/{name}</c> path (the client resolves it against the addon base). Text is plain.
/// </summary>
public sealed record SourceDetail(
    string Title,
    string? Subtitle = null,
    string? Overview = null,
    string? ImageUrl = null,
    IReadOnlyList<MetaFact>? Facts = null);
