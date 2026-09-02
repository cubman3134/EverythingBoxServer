using EverythingBox.Server.RomLibrary;

namespace EverythingBox.Server.Tests;

// ForRom is handed LOCAL filesystem paths -- the platform's own separator, straight from
// Directory.EnumerateFiles / ResolveSafeFile -- so every ROM path below is built with Path.Combine.
// A hard-coded @"C:\roms\..." literal is not a path at all on Linux, it is one long file name, and
// that is why these two cases used to fail there while the gamelist parsing beside them passed.
public class GamelistStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-gamelist-" + Guid.NewGuid().ToString("N"));
    public GamelistStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private void WriteGamelist(string xml) => File.WriteAllText(Path.Combine(_dir, "gamelist.xml"), xml);

    [Fact]
    public void Reads_fields_and_year_from_releasedate()
    {
        WriteGamelist(
            "<gameList><game>" +
            "<path>./Super Mario Bros.nes</path>" +
            "<name>Super Mario Bros.</name>" +
            "<desc>A plumber's adventure.</desc>" +
            "<releasedate>19960101T000000</releasedate>" +
            "<developer>Nintendo R&amp;D4</developer>" +
            "<publisher>Nintendo</publisher>" +
            "<genre>Platform</genre>" +
            "<players>2</players>" +
            "</game></gameList>");

        var idx = GamelistStore.Load(_dir);
        var e = idx.ForRom(Path.Combine(_dir, "Super Mario Bros.nes"));
        Assert.NotNull(e);
        Assert.Equal("Super Mario Bros.", e!.Name);
        Assert.Equal("A plumber's adventure.", e.Desc);
        Assert.Equal(1996, e.Year);
        Assert.Equal("Nintendo R&D4", e.Developer);
        Assert.Equal("Nintendo", e.Publisher);
        Assert.Equal("Platform", e.Genre);
        Assert.Equal("2", e.Players);
    }

    [Fact]
    public void ForRom_matches_by_filename_with_dot_slash_and_subdir_prefixes()
    {
        WriteGamelist(
            "<gameList>" +
            "<game><path>./root.nes</path><name>Root</name></game>" +
            "<game><path>subdir/nested.nes</path><name>Nested</name></game>" +
            "<game><path>.\\winsub\\written-on-windows.nes</path><name>Windows</name></game>" +
            "</gameList>");

        var idx = GamelistStore.Load(_dir);
        Assert.Equal("Root", idx.ForRom(Path.Combine(_dir, "root.nes"))!.Name);
        Assert.Equal("Nested", idx.ForRom(Path.Combine(_dir, "nested.nes"))!.Name);
        // A gamelist written by a Windows tool but read by a Linux install: a <path> separator is data,
        // not a local path, so the index normalises it whatever platform is doing the reading.
        Assert.Equal("Windows", idx.ForRom(Path.Combine(_dir, "written-on-windows.nes"))!.Name);
    }

    [Fact]
    public void Image_preferred_over_thumbnail()
    {
        WriteGamelist(
            "<gameList>" +
            "<game><path>a.nes</path><image>./art/a.png</image><thumbnail>./thumb/a.png</thumbnail></game>" +
            "<game><path>b.nes</path><thumbnail>./thumb/b.png</thumbnail></game>" +
            "</gameList>");

        var idx = GamelistStore.Load(_dir);
        Assert.Equal("./art/a.png", idx.ForRom("a.nes")!.ImageRelPath);
        Assert.Equal("./thumb/b.png", idx.ForRom("b.nes")!.ImageRelPath);
    }

    [Fact]
    public void Missing_gamelist_yields_empty()
    {
        var idx = GamelistStore.Load(_dir);
        Assert.Same(GamelistIndex.Empty, idx);
        Assert.Null(idx.ForRom("anything.nes"));
    }

    [Fact]
    public void Malformed_xml_yields_empty_without_throwing()
    {
        WriteGamelist("<gameList><game><path>oops.nes");
        var idx = GamelistStore.Load(_dir);
        Assert.Null(idx.ForRom("oops.nes"));
    }

    [Fact]
    public void A_DTD_entity_gamelist_is_refused_without_expanding_it()
    {
        // If the reader honored the DTD it would either expand &xxe; or throw on the external ref.
        // Hardened settings must make this yield an entry whose Name lacks the secret (or empty), never read the file.
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "TOPSECRET");
        WriteGamelist(
            "<?xml version=\"1.0\"?>" +
            $"<!DOCTYPE gameList [<!ENTITY xxe SYSTEM \"file://{secret.Replace("\\", "/")}\">]>" +
            "<gameList><game><path>x.nes</path><name>&xxe;</name></game></gameList>");

        var idx = GamelistStore.Load(_dir);
        var e = idx.ForRom("x.nes");
        // Either empty (DTD prohibited → throw → empty) or an entry with Name NOT containing the secret.
        Assert.True(e is null || !(e.Name ?? "").Contains("TOPSECRET"));
    }
}
