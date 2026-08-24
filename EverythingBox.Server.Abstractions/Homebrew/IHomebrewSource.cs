namespace EverythingBox.Server.Abstractions;

/// <summary>One homebrew title as it appears in a list of what exists for a system.
///
/// <c>Id</c> is a fully host-namespaced media id — the same kind of id a catalog row carries — so the
/// client plays it through the ordinary stream route rather than through anything this capability
/// provides. That is deliberate: a second download path would be a second thing to keep correct, for
/// nothing. Everything else is display material and every field but the title is optional, because a
/// source is only ever asked to state what it actually publishes and most publish less than all of it.
/// </summary>
public sealed record HomebrewTitle(
    string Id, string Title, string? Author, string? Version, string? Description, string? ImageUrl);

/// <summary>One page of titles. <c>NextCursor</c> is opaque to this server and to the client —
/// whatever the source needs to resume, in whatever shape suits it — and null when there is no more.
/// Nothing here parses it or assumes it survives being handed back to a different source.</summary>
public sealed record HomebrewListing(IReadOnlyList<HomebrewTitle> Items, string? NextCursor);

/// <summary>Homebrew available for a retro system. Implemented by a plugin and registered via
/// <see cref="IPluginRegistry.AddHomebrewSource"/>.
///
/// This server holds no opinion about where homebrew comes from: a source is whatever a plugin
/// supplies, and the call is best-effort — an unreachable or unmapped source returns an empty page
/// rather than failing the request, because "none for this console" and "the source is down" look the
/// same to someone browsing and neither is an error worth breaking a page over.
///
/// Listing is the WHOLE capability. There is deliberately no fetch or resolve companion to
/// <see cref="ListAsync"/>: a row's <see cref="HomebrewTitle.Id"/> is already a playable media id, so
/// the existing stream route hands the bytes over and this surface never has to. A second download
/// path would be another thing to keep correct, and it would buy nothing the first one does not
/// already do.</summary>
public interface IHomebrewSource
{
    /// <summary>One page of homebrew for a system, by the client's system id ("nds", "gba"), resuming
    /// from <paramref name="cursor"/> when one is given. Empty when the system is unmapped, or the
    /// source is unreachable.</summary>
    Task<HomebrewListing> ListAsync(string systemId, string? cursor, CancellationToken ct);
}
