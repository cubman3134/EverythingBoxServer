namespace EverythingBox.Server.Abstractions;

/// <summary>A romhack as it appears in a list of what is available for a game.</summary>
/// <param name="Id">Opaque to the client and to this server: only the plugin that produced it knows
/// how to resolve it. Treat it as a key, never as a URL.</param>
/// <param name="Source">Which provider produced this row, for display beside the title.</param>
/// <param name="Category">"Translation", "Hack", or whatever the provider distinguishes — the first
/// thing a person filters on.</param>
public sealed record RomhackInfo(
    string Id, string Source, string Title, string ReleasedBy, string? Version, string Category,
    string? Language, string? Genre, string? Date);

/// <summary>A fetched romhack: the patch itself plus whatever the provider can say about what it
/// targets.</summary>
/// <param name="PatchFormat">What the patch's own magic bytes say it is ("ips", "bps", "ups"), NOT
/// what any listing claimed. The client decides the same way and would refuse a mislabelled patch.</param>
/// <param name="TargetNote">The provider's free-text hint about the dump the author built against —
/// a container description, a readme excerpt, or both. There is no guarantee any of this exists, and
/// no guarantee it is machine-comparable; it is material for the client to SHOW a person, not to
/// decide with.</param>
/// <param name="Patches">One entry per patch in the release. More than one means the hack ships a
/// patch per ROM revision, and the client must ask which rather than pick.</param>
public sealed record RomhackPatchSet(
    string Id, string? Version, string? TargetNote, IReadOnlyList<RomhackPatch> Patches);

/// <param name="Name">The patch file's own name, which is usually how a multi-patch release tells
/// its revisions apart.</param>
public sealed record RomhackPatch(string Name, string PatchFormat, byte[] Bytes);

/// <summary>Romhacks available for a retro game, and the patches behind them. Implemented by a
/// plugin and registered via <see cref="IPluginRegistry.AddRomhackSource"/>.
///
/// This server holds no opinion about where hacks come from: a source is whatever a plugin supplies,
/// and both calls are best-effort — an unreachable or unmapped source returns empty or null rather
/// than failing the request, because "no hacks for this game" and "the source is down" look the same
/// to someone browsing and neither is an error worth breaking a page over.
///
/// Nothing here verifies that a patch fits the ROM the client holds. It cannot: the client is the
/// only side that has the ROM. Patch formats that embed a source checksum are refused client-side on
/// that checksum; the rest is what <see cref="RomhackPatchSet.TargetNote"/> is for.</summary>
public interface IRomhackSource
{
    /// <summary>Hacks for a game, identified by the client's system id and the game's title. Empty
    /// when the system is unmapped, the game is unknown, or the source is unreachable.</summary>
    Task<IReadOnlyList<RomhackInfo>> ListAsync(string systemId, string title, CancellationToken ct);

    /// <summary>The patches behind one hack, by the id a <see cref="RomhackInfo"/> carried. Null when
    /// the id is unknown to every registered source, or the source is unreachable.</summary>
    Task<RomhackPatchSet?> FetchAsync(string id, CancellationToken ct);
}
