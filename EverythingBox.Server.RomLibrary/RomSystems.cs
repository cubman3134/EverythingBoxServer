namespace EverythingBox.Server.RomLibrary;

/// <summary>
/// Maps a ROM folder name to a canonical (systemId, consoleTitle). The <b>title</b> is contract-critical:
/// the EverythingBox client derives which emulator/core to use from the parent platform item's title
/// (its console-name matcher), not from any game field — so each title is a recognizable console name.
/// Data-only, intentionally duplicated from the client's system list because a public plugin cannot
/// reference the client. An unrecognized folder is not an error: the caller lists it under its own name.
/// </summary>
internal static class RomSystems
{
    // Normalize a folder name the way the client's aliases are written: letters/digits only, lowercased.
    // "Mega Drive" -> "megadrive", "Sega-32X" -> "sega32x".
    private static string Norm(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s) if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    // normalized folder alias -> (systemId, consoleTitle)
    private static readonly Dictionary<string, (string Id, string Title)> Map = BuildMap();

    private static Dictionary<string, (string, string)> BuildMap()
    {
        var m = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        void Add(string id, string title, params string[] aliases)
        { foreach (var a in aliases) m[Norm(a)] = (id, title); }

        Add("nes", "Nintendo Entertainment System", "nes", "famicom", "fc");
        Add("snes", "Super Nintendo Entertainment System", "snes", "superfamicom", "sfc", "supernintendo");
        Add("n64", "Nintendo 64", "n64", "nintendo64");
        Add("gb", "Nintendo Game Boy", "gb", "gameboy", "gbc", "gameboycolor");
        Add("gba", "Nintendo Game Boy Advance", "gba", "gameboyadvance");
        Add("nds", "Nintendo DS", "nds", "ds", "nintendods");
        Add("gc", "Nintendo GameCube", "gc", "gamecube", "ngc");
        Add("virtualboy", "Nintendo Virtual Boy", "virtualboy", "vb");
        Add("genesis", "Sega Genesis", "genesis", "megadrive", "md", "segagenesis", "segamegadrive");
        Add("genesis", "Sega Master System", "mastersystem", "sms", "segamastersystem");
        Add("genesis", "Sega Game Gear", "gamegear", "gg", "segagamegear");
        Add("32x", "Sega 32X", "32x", "sega32x");
        Add("segacd", "Sega CD", "segacd", "megacd");
        Add("saturn", "Sega Saturn", "saturn", "segasaturn");
        Add("dreamcast", "Sega Dreamcast", "dreamcast", "dc", "segadreamcast");
        Add("psx", "Sony PlayStation", "psx", "ps1", "playstation", "psone");
        Add("ps2", "Sony PlayStation 2", "ps2", "playstation2");
        Add("psp", "Sony PSP", "psp", "playstationportable");
        Add("pce", "PC Engine", "pce", "pcengine", "tg16", "turbografx16", "turbografx");
        Add("neogeo", "SNK Neo Geo", "neogeo", "neogeoaes", "neogeomvs");
        Add("ws", "WonderSwan", "ws", "wonderswan", "wsc", "wonderswancolor");
        Add("ngp", "Neo Geo Pocket", "ngp", "neogeopocket", "ngpc");
        Add("lynx", "Atari Lynx", "lynx", "atarilynx");
        Add("a2600", "Atari 2600", "a2600", "atari2600");
        Add("a7800", "Atari 7800", "a7800", "atari7800");
        Add("c64", "Commodore 64", "c64", "commodore64");
        Add("amiga", "Commodore Amiga", "amiga", "commodoreamiga");
        Add("msdos", "MS-DOS", "dos", "msdos", "pc");
        return m;
    }

    /// <summary>Resolve a folder name to its canonical (systemId, consoleTitle), or null if unknown.</summary>
    public static (string Id, string Title)? Resolve(string folderName)
        => Map.TryGetValue(Norm(folderName), out var v) ? v : null;
}
