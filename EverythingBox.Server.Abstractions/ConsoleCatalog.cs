namespace EverythingBox.Server.Abstractions;

/// <summary>A known console: its display name, alternate names, and ROM extensions.</summary>
public sealed record RetroConsole(string Name, IReadOnlyList<string> Aliases, IReadOnlyList<string> Extensions);

/// <summary>
/// A factual table of retro game consoles: canonical names, common aliases, and the
/// ROM file extensions each system's dumps use. Useful to any emulator front end for
/// normalising a console name/alias to its canonical form, or for detecting which
/// console a free-text query is naming.
/// </summary>
/// <remarks>
/// This lives in Abstractions, not Core, deliberately: a plugin references only the
/// contract assembly, so a helper it needs cannot live in Core. It has no imports at
/// all — a plain data table — so it carries no Core dependency.
/// </remarks>
public static class ConsoleCatalog
{
    /// <summary>Built-in consoles, with common aliases and their ROM file extensions.</summary>
    public static readonly IReadOnlyList<RetroConsole> Defaults =
    [
        new("Nintendo Entertainment System", ["NES", "Famicom", "FC"], [".nes", ".unf", ".fds"]),
        // The Famicom Disk System (disk add-on) is a distinct library of .fds disk images, separate
        // from the NES cartridge library despite the shared "Famicom" name. Listed here so
        // DetectFromQuery's longest-match rule prefers "Famicom Disk System" over the "Famicom" alias.
        new("Famicom Disk System", ["FDS", "Famicom Disk System"], [".fds"]),
        new("Super Nintendo", ["SNES", "Super Famicom", "SFC", "Super Nintendo Entertainment System"], [".sfc", ".smc"]),
        new("Nintendo 64", ["N64"], [".z64", ".n64", ".v64"]),
        new("Nintendo GameCube", ["GameCube", "GCN", "NGC"], [".iso", ".rvz", ".gcm", ".ciso"]),
        new("Nintendo Wii", ["Wii"], [".iso", ".wbfs", ".rvz", ".wad"]),
        new("Nintendo Switch", ["Switch", "NSW"], [".nsp", ".xci", ".nca", ".nro"]),
        new("Game Boy", ["GB"], [".gb"]),
        new("Game Boy Color", ["GBC"], [".gbc"]),
        new("Game Boy Advance", ["GBA"], [".gba"]),
        new("Nintendo DS", ["NDS", "DS"], [".nds"]),
        new("Nintendo 3DS", ["3DS"], [".3ds", ".cia"]),
        new("Sega Genesis", ["Genesis", "Mega Drive", "Megadrive", "Sega Mega Drive", "MD"], [".md", ".gen", ".bin", ".smd"]),
        new("Sega Master System", ["Master System", "SMS"], [".sms"]),
        new("Sega Game Gear", ["Game Gear", "GG"], [".gg"]),
        new("Sega SG-1000", ["SG-1000", "SG1000"], [".sg"]),
        new("Sega Saturn", ["Saturn"], [".chd", ".cue", ".iso"]),
        new("Sega Dreamcast", ["Dreamcast", "DC"], [".chd", ".gdi", ".cdi"]),
        new("Sega CD", ["Mega CD", "Sega Mega CD"], [".chd", ".cue", ".iso"]),
        new("Sony PlayStation", ["PlayStation", "PS1", "PSX", "PSone"], [".chd", ".bin", ".cue", ".pbp", ".iso"]),
        new("Sony PlayStation 2", ["PlayStation 2", "PS2"], [".iso", ".chd", ".bin"]),
        new("Sony PSP", ["PSP", "PlayStation Portable"], [".iso", ".cso", ".chd"]),
        // Modern Sony systems: releases are typically a single .pkg (PS4/PS3 PSN), a disc
        // .iso (PS3), or a .vpk (Vita).
        new("Sony PlayStation 3", ["PlayStation 3", "PS3"], [".pkg", ".iso"]),
        new("Sony PlayStation 4", ["PlayStation 4", "PS4"], [".pkg", ".iso"]),
        new("Sony PlayStation Vita", ["PlayStation Vita", "PS Vita", "PSVita", "Vita"], [".vpk", ".mai", ".zip"]),
        new("Panasonic 3DO", ["3DO", "Panasonic 3DO"], [".chd", ".iso", ".cue", ".bin"]),
        new("Atari 2600", ["Atari 2600", "VCS"], [".a26", ".bin"]),
        new("Atari 7800", ["Atari 7800"], [".a78"]),
        new("Atari Lynx", ["Lynx"], [".lnx"]),
        // SuperGrafx is a PC Engine expansion with its own library (.sgx) that many front ends
        // fold into the same emulator core as the base TurboGrafx-16/PC Engine, hence the
        // combined alias and shared .sgx/.pce extensions on both entries below.
        new("TurboGrafx-16", ["TurboGrafx", "PC Engine", "PCE", "PC Engine / TurboGrafx-16", "PC Engine SuperGrafx"], [".pce", ".sgx"]),
        new("SuperGrafx", ["SuperGrafx", "SGX"], [".sgx", ".pce"]),
        new("Neo Geo", ["NeoGeo"], [".neo", ".zip"]),
        new("WonderSwan", ["WonderSwan Color", "WS", "WSC"], [".ws", ".wsc"]),
        new("Nintendo Virtual Boy", ["Virtual Boy", "VB"], [".vb", ".vboy"]),
        new("Nintendo Wii U", ["Wii U", "WiiU"], [".wud", ".wux", ".wua", ".rpx", ".iso"]),
        new("Microsoft Xbox", ["Xbox", "Original Xbox"], [".iso", ".xiso"]),
        new("Microsoft Xbox 360", ["Xbox 360", "X360"], [".iso", ".god", ".zar"]),
        new("Atari 5200", ["Atari 5200"], [".a52", ".bin"]),
        new("Atari Jaguar", ["Jaguar", "Atari Jaguar"], [".j64", ".jag", ".abs", ".cof", ".rom"]),
        new("Neo Geo Pocket", ["NeoGeo Pocket", "NGP"], [".ngp"]),
        new("Neo Geo Pocket Color", ["NeoGeo Pocket Color", "NGPC"], [".ngc", ".ngpc"]),
        new("PC-FX", ["PCFX", "PC-FX"], [".chd", ".cue", ".ccd"]),
        new("PC Engine CD", ["TurboGrafx-CD", "PC Engine CD", "PCE CD", "PCECD"], [".chd", ".cue", ".ccd"]),
        new("Sega 32X", ["32X", "Sega 32X"], [".32x"]),
        new("Mattel Intellivision", ["Intellivision", "INTV"], [".int", ".bin"]),
        new("Commodore 64", ["C64", "Commodore 64"], [".d64", ".t64", ".prg", ".crt", ".g64"]),
        new("Commodore Amiga", ["Amiga", "Commodore Amiga"], [".adf", ".adz", ".hdf", ".lha", ".ipf"]),
        // Apple II disk images are commonly distributed as a .zip wrapping the actual disk
        // image (.dsk/.do/.woz), so both the archive and disk-image extensions are listed.
        new("Apple II", ["Apple II", "Apple 2", "AppleII", "Apple ]["], [".dsk", ".woz", ".do", ".po", ".2mg", ".nib"]),
        // Atari ST disk images are typically distributed as loose .st/.stx files.
        new("Atari ST", ["Atari ST", "AtariST"], [".st", ".stx", ".msa", ".dim", ".ipf"]),
        new("Arcade (MAME)", ["Arcade", "MAME"], [".zip", ".chd"]),
    ];

    /// <summary>The canonical console name for a name/alias (e.g. "NES" → "Nintendo
    /// Entertainment System"); the input trimmed if unrecognised.</summary>
    public static string CanonicalName(string console)
        => Defaults.FirstOrDefault(c => Matches(c, console))?.Name ?? console.Trim();

    /// <summary>
    /// Detects a known console name/alias as a trailing word-run in free text (e.g.
    /// "Super Mario World SNES" → "Super Nintendo"), preferring the longest known name so
    /// e.g. "Famicom Disk System" wins over the "Famicom" alias. Returns the canonical
    /// console name, or null if none is recognised.
    /// </summary>
    public static string? DetectFromQuery(string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return null;

        // All recognisable names (built-in names + aliases), longest first so
        // "Super Nintendo" wins over "Nintendo".
        var names = new List<string>();
        foreach (var c in Defaults)
        {
            names.Add(c.Name);
            names.AddRange(c.Aliases);
        }

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).OrderByDescending(n => n.Length))
        {
            // Match as a trailing token-run: "<game> <name>".
            if (q.Length > name.Length
                && q.EndsWith(name, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(q[q.Length - name.Length - 1]))
            {
                return CanonicalName(name);
            }
        }

        return null;
    }

    private static bool Matches(RetroConsole c, string input)
        => string.Equals(c.Name, input, StringComparison.OrdinalIgnoreCase)
           || c.Aliases.Any(a => string.Equals(a, input, StringComparison.OrdinalIgnoreCase));
}
