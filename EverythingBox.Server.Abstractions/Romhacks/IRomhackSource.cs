namespace EverythingBox.Server.Abstractions;

/// <summary>A romhack as it appears in a list of what is available for a game.
///
/// <c>Id</c> is opaque to the client and to this server: only the plugin that produced it knows how to
/// resolve it, so treat it as a key and never as a URL. <c>Source</c> names the provider the row came
/// from, for display beside the title. <c>Category</c> is "Translation", "Hack", or whatever else the
/// provider distinguishes — the first thing a person filters on.</summary>
public sealed record RomhackInfo(
    string Id, string Source, string Title, string ReleasedBy, string? Version, string Category,
    string? Language, string? Genre, string? Date);

/// <summary>A fetched romhack: the patch itself plus whatever the provider can say about what it
/// targets.
///
/// <c>TargetNote</c> is the provider's free-text hint about the dump the author built against — a
/// container description, a readme excerpt, or both. There is no guarantee any of it exists and no
/// guarantee it is machine-comparable: it is material for the client to SHOW a person, not to decide
/// with. <c>Patches</c> holds one entry per patch in the release; more than one means the hack ships a
/// patch per ROM revision, and the client must ask which rather than pick.</summary>
public sealed record RomhackPatchSet(
    string Id, string? Version, string? TargetNote, IReadOnlyList<RomhackPatch> Patches)
{
    /// <summary>What the patch was built against, when the source actually states it. Optional because most
    /// sources do not: the field exists to carry a FACT a source published, never a guess made on its behalf.
    /// </summary>
    public RomhackTarget? Target { get; init; }
}

/// <summary>The dump a patch was built against, as its source stated it. Every field is optional and any of
/// them may be the only one present.
///
/// This is the answer to the question that otherwise cannot be answered. IPS carries no checksum and applies
/// cleanly to any bytes at all, so a patch built for a different dump of the same game produces a broken game
/// with nothing to catch it. The usual way that happens is regional: a translation is built against the
/// release that needed translating — normally the Japanese one — and not the English release most libraries
/// hold.
///
/// <c>Crc32</c> and <c>Sha1</c> are lowercase hex of the ORIGINAL, unpatched ROM, so a client holding the ROM
/// can check before applying. <c>FileName</c> is the dump's catalogued name ("Final Fantasy V (Japan).sfc"),
/// which names the release in a form a person recognises even when they cannot hash anything. <c>Region</c>
/// is a short marker ("J", "U", "E") for sources that identify a release only that far.</summary>
public sealed record RomhackTarget(string? FileName, string? Crc32, string? Sha1, string? Region);

/// <summary>One file in a release. <c>Url</c> is where to fetch it, NOT the bytes: a patch may be a
/// 14-byte IPS or a 1.4 GB pre-applied disc image, and the same response shape has to carry both.
/// Embedding the file put a measured 5,286 MB into the server for one ROM and offered no Range, so no
/// resume either — a dropped connection meant starting over.
///
/// <para><c>PatchFormat</c> still comes from the source, never from the bytes: a source that says
/// "rom" is asserting the file is a playable ROM, and nothing sniffed can assert that.</para></summary>
public sealed record RomhackPatch(string Name, string PatchFormat, string Url);

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
