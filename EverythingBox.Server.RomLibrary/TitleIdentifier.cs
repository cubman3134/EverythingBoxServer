namespace EverythingBox.Server.RomLibrary;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class TitleIdentifier
{
    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9A-Fa-f]{16})(?![0-9A-Fa-f])")] private static partial Regex Hex16();
    [GeneratedRegex(@"\[v(\d+)\]|\bv(\d+)\b", RegexOptions.IgnoreCase)] private static partial Regex Version();
    [GeneratedRegex(@"\b(update|patch|upd)\b", RegexOptions.IgnoreCase)] private static partial Regex UpdateWord();
    [GeneratedRegex(@"\b(dlc|add[- ]?on)\b", RegexOptions.IgnoreCase)] private static partial Regex DlcWord();
    [GeneratedRegex(@"([A-Z]{4}\d{5})", RegexOptions.IgnoreCase)] private static partial Regex Ps3Code();
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")] private static partial Regex TagRegion();

    /// <summary>Best identity for a file, or null if nothing plausible. Order: a 16-hex Switch/WiiU/3DS
    /// title id (the arithmetic relationship) → a PS3 .pkg header / PS3 game code in the name → generic
    /// update/DLC keywords. Null when no signal — the caller treats it as its own singleton.</summary>
    public static PackageIdentity? Identify(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var version = ParseVersion(name);

        // 1) 16-hex title id (Switch / Wii U / 3DS). The low bits place the file in its title.
        if (Hex16().Match(name) is { Success: true } h)
        {
            var id = h.Groups[1].Value.ToUpperInvariant();
            var high8 = id[..8];
            // Wii U / 3DS: the high 8 hex are the type; the base swaps them to the app type.
            if (high8 is "0005000E" or "0005000C")   // Wii U update / DLC
                return new PackageIdentity("00050000" + id[8..], high8 == "0005000E" ? TitleKind.Update : TitleKind.Dlc, version);
            if (high8 is "0004000E" or "0004008C")   // 3DS update / DLC
                return new PackageIdentity("00040000" + id[8..], high8 == "0004000E" ? TitleKind.Update : TitleKind.Dlc, version);
            // Switch (and Wii U/3DS base): a base application id always has its low 16 bits zero
            // ("…0000"). Its update is base|0x800 ("…0800"); its DLC is base+0x1000·n ("…1000", "…2000", …),
            // so the DLC marker sits in the 0x1000-place nibble — the base is the low 4 nibbles zeroed.
            var baseId = id[..12] + "0000";
            var last3 = id[13..];   // low 3 hex nibbles
            var kind = last3 == "800" ? TitleKind.Update
                     : last3 == "000" && id[12] == '0' ? TitleKind.Base
                     : TitleKind.Dlc;
            return new PackageIdentity(baseId, kind, version);
        }

        // 2) PS3: prefer the .pkg header content-id; else a game code in the name.
        var ps3Id = ext == ".pkg" ? Ps3PkgReader.TryReadTitleId(path) : null;
        ps3Id ??= Ps3Code().Match(name) is { Success: true } p ? p.Groups[1].Value.ToUpperInvariant() : null;
        if (ps3Id is not null)
            return new PackageIdentity(ps3Id.ToUpperInvariant(), KindFromWords(name), version);

        // 3) Generic keyword fallback — only meaningful once a base with the SAME stem exists (the grouper
        // decides). Group key = the title stem with version/update/DLC markers stripped.
        var kindW = KindFromWords(name);
        if (kindW != TitleKind.Base)
            return new PackageIdentity(NormalizeStem(name), kindW, version);

        return null;   // a plain, unmarked file → no identity; the grouper makes it a singleton
    }

    private static TitleKind KindFromWords(string name)
        => DlcWord().IsMatch(name) ? TitleKind.Dlc : UpdateWord().IsMatch(name) ? TitleKind.Update : TitleKind.Base;
    private static int? ParseVersion(string name)
    { var m = Version().Match(name); if (!m.Success) return null; var g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value; return int.TryParse(g, out var v) ? v : null; }
    // strip (…)/[…] tags, version/update/dlc words → a normalizing stem so a base and its keyword-marked
    // update share a group key. Lowercased, alphanumerics only (mirror the client's cleanTitle intent).
    private static string NormalizeStem(string name)
    {
        var s = TagRegion().Replace(name, " ");
        s = UpdateWord().Replace(s, " ");
        s = DlcWord().Replace(s, " ");
        s = Version().Replace(s, " ");
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
