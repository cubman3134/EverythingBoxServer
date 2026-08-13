namespace EverythingBox.Server.RomLibrary;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class TitleIdentifier
{
    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9A-Fa-f]{16})(?![0-9A-Fa-f])")] private static partial Regex Hex16();
    [GeneratedRegex(@"\[v(\d+)\]|\bv(\d+)\b", RegexOptions.IgnoreCase)] private static partial Regex Version();
    [GeneratedRegex(@"\b(update|patch|upd)\b", RegexOptions.IgnoreCase)] private static partial Regex UpdateWord();
    [GeneratedRegex(@"\b(dlc|add[- ]?on)\b", RegexOptions.IgnoreCase)] private static partial Regex DlcWord();
    [GeneratedRegex(@"([A-Z]{4}\d{5})")] private static partial Regex Ps3Code();
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")] private static partial Regex TagRegion();

    /// <summary>Best identity for a file, or null if nothing plausible. Order: a 16-hex Switch/WiiU/3DS
    /// title id (the arithmetic relationship) → a PS3 .pkg header / PS3 game code in the name → generic
    /// update/DLC keywords. Null when no signal — the caller treats it as its own singleton.</summary>
    public static PackageIdentity? Identify(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var version = ParseVersion(name);

        // 1) 16-hex title id (Switch / Wii U / 3DS) — ONLY for recognized title-id shapes. Wii U and 3DS
        // encode the type in the HIGH 8 hex (the unique id is the low 8, never zeroed); Switch encodes it in
        // the LOW 4. An unrecognized 16-hex chunk (e.g. a hash fragment) is NOT a title id — group nothing
        // when unsure: fall through to the PS3 + keyword branches rather than mint a bogus id.
        if (Hex16().Match(name) is { Success: true } h)
        {
            var id = h.Groups[1].Value.ToUpperInvariant();
            var high8 = id[..8];
            switch (high8)
            {
                // Wii U: base id = 00050000 + the low 8 hex (a base's own id IS that, verbatim → they group).
                case "00050000": return new PackageIdentity(id, TitleKind.Base, version);
                case "0005000E": return new PackageIdentity("00050000" + id[8..], TitleKind.Update, version);
                case "0005000C": return new PackageIdentity("00050000" + id[8..], TitleKind.Dlc, version);
                // 3DS: base id = 00040000 + the low 8 hex.
                case "00040000": return new PackageIdentity(id, TitleKind.Base, version);
                case "0004000E": return new PackageIdentity("00040000" + id[8..], TitleKind.Update, version);
                case "0004008C": return new PackageIdentity("00040000" + id[8..], TitleKind.Dlc, version);
            }
            // Switch: an 01-prefixed id. The base zeroes the low 4 nibbles; those low 4 set the kind —
            // "0000" base, "0800" update, anything else DLC (a DLC id ends 1000/1800/2800/…). Requiring the
            // FULL low-4 "0800" for Update keeps a DLC ending 1800/2800 out of the Update bucket.
            if (id.StartsWith("01", StringComparison.Ordinal))
            {
                var baseId = id[..12] + "0000";
                var low4 = id[12..];
                var kind = low4 == "0000" ? TitleKind.Base
                         : low4 == "0800" ? TitleKind.Update
                         : TitleKind.Dlc;
                return new PackageIdentity(baseId, kind, version);
            }
            // Any other 16-hex prefix → not a title id; fall through.
        }

        // 2) PS3: prefer the .pkg header content-id; else a game code in the name.
        var ps3Id = ext == ".pkg" ? Ps3PkgReader.TryReadTitleId(path) : null;
        ps3Id ??= Ps3Code().Match(name) is { Success: true } p ? p.Groups[1].Value.ToUpperInvariant() : null;
        if (ps3Id is not null)
            return new PackageIdentity(ps3Id.ToUpperInvariant(), KindFromWords(name), version);

        // 3) Generic keyword fallback — kind-LABELS an update/DLC-marked file only; it can never FORM a
        // group on its own. A plain unmarked base returns null (below), so a stem bucket never holds a Base,
        // and without a title-id or PS3 signal even a marked file ends up a singleton in the grouper. We
        // deliberately do NOT give unmarked files a stem Base identity — that would over-group unrelated
        // files that merely share a normalized stem. Group key (for a marked file) = that stripped stem.
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
