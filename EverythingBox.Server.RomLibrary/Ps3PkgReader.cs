namespace EverythingBox.Server.RomLibrary;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Reads a PS3 .pkg's content-id from the unencrypted header (offset 0x30, 36 ASCII bytes),
/// e.g. "UP0001-BLES01807_00-0000000000000000", and extracts the game code ("BLES01807"). The decisive
/// base/patch/DLC category lives in an often-encrypted PARAM.SFO, so it is NOT read here — the game code
/// (the group key) is all we need unencrypted. Any malformed/short file → null, never throws.</summary>
internal static partial class Ps3PkgReader
{
    [GeneratedRegex(@"-([A-Z]{4}\d{5})_")] private static partial Regex GameCode();

    public static string? TryReadTitleId(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: false);
            Span<byte> magic = stackalloc byte[4];
            fs.ReadExactly(magic);   // throws on a short read → caught below → null
            if (magic[0] != 0x7F || magic[1] != (byte)'P' || magic[2] != (byte)'K' || magic[3] != (byte)'G')
                return null;
            fs.Seek(0x30, SeekOrigin.Begin);
            Span<byte> cid = stackalloc byte[36];
            fs.ReadExactly(cid);
            var text = Encoding.ASCII.GetString(cid).TrimEnd('\0', ' ');
            var m = GameCode().Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; } // missing / short / IO — not a PKG we can read
    }
}
