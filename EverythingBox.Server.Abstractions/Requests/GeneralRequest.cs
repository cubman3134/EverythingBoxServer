namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A free-form search with a caller-chosen media type, for "search anything"
/// scenarios. Defaults to <see cref="MediaType.Other"/>, which applies no
/// category scoping or cross-type filtering.
/// </summary>
public sealed class GeneralRequest : MediaRequest
{
    /// <summary>The media type to search as. <see cref="MediaType.Other"/> = any.</summary>
    public MediaType Kind { get; init; } = MediaType.Other;

    public override MediaType MediaType => Kind;

    /// <summary>
    /// Restrict in-pack file selection to this file type/extension, e.g. "iso",
    /// "pdf", "mkv". Affects which file(s) are pulled from a multi-file pack, not
    /// the item search. For more than one acceptable type, use <see cref="FileTypes"/>.
    /// </summary>
    public string? FileType { get; init; }

    /// <summary>
    /// Restrict in-pack file selection to <em>any</em> of these file types/extensions,
    /// e.g. [".sfc", ".smc"] for a console with several ROM formats. Takes precedence
    /// over <see cref="FileType"/> when non-empty.
    /// </summary>
    public IReadOnlyList<string> FileTypes { get; init; } = [];

    /// <summary>
    /// "Tags" for picking specific file(s) out of a pack — each matched against file
    /// names independently of <see cref="MediaRequest.Title"/>. Lets you find an
    /// item by its title but target one or more files inside it (for example, picking
    /// three files out of a several-thousand-file remote collection). A file is
    /// selected if it matches <em>any</em> tag. Falls back to the title when empty.
    /// </summary>
    public IReadOnlyList<string> FileFilters { get; init; } = [];
}
