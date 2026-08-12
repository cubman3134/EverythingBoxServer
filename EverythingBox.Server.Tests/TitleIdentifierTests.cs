using System.Text;
using EverythingBox.Server.RomLibrary;
using Xunit;

namespace EverythingBox.Server.Tests;

public class TitleIdentifierTests
{
    // ---- Switch: base / update / DLC share the base id, low 3 nibbles set the kind ----

    [Fact]
    public void Switch_base_update_dlc_share_base_id_with_right_kinds()
    {
        var b = TitleIdentifier.Identify("Some Game [0100AAAABBBB0000].nsp");
        Assert.Equal(new PackageIdentity("0100AAAABBBB0000", TitleKind.Base, null), b);

        var u = TitleIdentifier.Identify("Some Game [0100AAAABBBB0800].nsp");
        Assert.Equal(new PackageIdentity("0100AAAABBBB0000", TitleKind.Update, null), u);

        var d = TitleIdentifier.Identify("Some Game [0100AAAABBBB1000].nsp");
        Assert.Equal(new PackageIdentity("0100AAAABBBB0000", TitleKind.Dlc, null), d);
    }

    // ---- Wii U: high8 0005000E→update, 0005000C→DLC, base id = 00050000 + low8 ----

    [Fact]
    public void WiiU_update_and_dlc_map_to_base_program_id()
    {
        var u = TitleIdentifier.Identify("Game [0005000E12345678].wud");
        Assert.Equal(new PackageIdentity("0005000012345678", TitleKind.Update, null), u);

        var d = TitleIdentifier.Identify("Game [0005000C12345678].wud");
        Assert.Equal(new PackageIdentity("0005000012345678", TitleKind.Dlc, null), d);
    }

    // ---- 3DS: high8 0004000E→update, base id = 00040000 + low8 ----

    [Fact]
    public void ThreeDS_update_maps_to_base_program_id()
    {
        var u = TitleIdentifier.Identify("Game [0004000E00123400].cia");
        Assert.Equal(new PackageIdentity("0004000000123400", TitleKind.Update, null), u);
    }

    // ---- PS3: the unencrypted .pkg header content-id OVERRIDES a misleading filename ----

    [Fact]
    public void Ps3_pkg_header_content_id_overrides_filename()
    {
        var path = WritePkg("Misleading Name (BLUS99999).pkg", MakePkgBytes("UP0001-BLES01807_00-0000000000000000"));
        try
        {
            var id = TitleIdentifier.Identify(path);
            Assert.NotNull(id);
            Assert.Equal("BLES01807", id!.TitleId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ps3_pkg_reader_reads_game_code_from_header()
    {
        var path = WritePkg("anything.pkg", MakePkgBytes("UP0001-BLES01807_00-0000000000000000"));
        try { Assert.Equal("BLES01807", Ps3PkgReader.TryReadTitleId(path)); }
        finally { File.Delete(path); }
    }

    // ---- PS3: a non-pkg-body .pkg falls back to the name's game code + keyword kind ----

    [Fact]
    public void Ps3_non_pkg_body_falls_back_to_name_game_code_and_update_keyword()
    {
        // A .pkg whose bytes are NOT a real PKG header → header read returns null, name wins.
        var path = WritePkg("Game (BLES01807) UPDATE.pkg", Encoding.ASCII.GetBytes("not a real pkg body"));
        try
        {
            var id = TitleIdentifier.Identify(path);
            Assert.NotNull(id);
            Assert.Equal("BLES01807", id!.TitleId);
            Assert.Equal(TitleKind.Update, id.Kind);
        }
        finally { File.Delete(path); }
    }

    // ---- Version parsing ----

    [Theory]
    [InlineData("Game [0100AAAABBBB0000] [v65536].nsp", 65536)]
    [InlineData("Game [0100AAAABBBB0000] v131072.nsp", 131072)]
    public void Version_marker_is_parsed_as_int(string name, int expected)
    {
        var id = TitleIdentifier.Identify(name);
        Assert.NotNull(id);
        Assert.Equal(expected, id!.Version);
    }

    // ---- No signal → null ----

    [Fact]
    public void Plain_unmarked_file_has_no_identity()
        => Assert.Null(TitleIdentifier.Identify("Some Game.iso"));

    // ---- Malformed / truncated .pkg → null, never throws ----

    [Fact]
    public void Malformed_pkg_returns_null_without_throwing()
    {
        var wrongMagic = WritePkg("bad.pkg", Encoding.ASCII.GetBytes("ZZZZ and some more bytes here...................."));
        var truncated = WritePkg("short.pkg", new byte[] { 0x7F, (byte)'P', (byte)'K', (byte)'G', 0x00, 0x01 });
        try
        {
            Assert.Null(Ps3PkgReader.TryReadTitleId(wrongMagic));
            Assert.Null(Ps3PkgReader.TryReadTitleId(truncated));
            Assert.Null(Ps3PkgReader.TryReadTitleId(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".pkg")));
        }
        finally { File.Delete(wrongMagic); File.Delete(truncated); }
    }

    // ---- helpers ----

    // Build a minimal unencrypted PKG: magic 7F 50 4B 47 at 0, content-id (null-padded to 36) at 0x30.
    private static byte[] MakePkgBytes(string contentId)
    {
        var buf = new byte[0x30 + 36];
        buf[0] = 0x7F; buf[1] = (byte)'P'; buf[2] = (byte)'K'; buf[3] = (byte)'G';
        var cid = Encoding.ASCII.GetBytes(contentId);
        Array.Copy(cid, 0, buf, 0x30, Math.Min(cid.Length, 36)); // remainder stays 0x00 (null padding)
        return buf;
    }

    private static string WritePkg(string fileName, byte[] bytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ebs22-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
