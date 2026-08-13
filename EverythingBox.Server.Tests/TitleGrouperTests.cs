using System.Text;
using EverythingBox.Server.RomLibrary;
using Xunit;

namespace EverythingBox.Server.Tests;

public class TitleGrouperTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // ---- base + update + DLC sharing a title id → ONE group ----

    [Fact]
    public void Base_update_dlc_form_one_group_headed_by_the_base()
    {
        var b = "Some Game [0100AAAABBBB0000].nsp";
        var u = "Some Game [0100AAAABBBB0800].nsp";
        var d = "Some Game [0100AAAABBBB1000].nsp";

        var groups = TitleGrouper.Group([u, b, d]);

        var g = Assert.Single(groups);
        Assert.Equal("0100AAAABBBB0000", g.BaseTitleId);
        Assert.Equal(b, g.BasePath);
        Assert.Equal(u, Assert.Single(g.Updates).Path);
        Assert.Equal(d, Assert.Single(g.Dlc).Path);
    }

    // ---- two updates → newest version first ----

    [Fact]
    public void Two_updates_are_ordered_newest_first()
    {
        var b = "Some Game [0100AAAABBBB0000].nsp";
        var older = "Some Game [0100AAAABBBB0800] [v65536].nsp";
        var newer = "Some Game [0100AAAABBBB0800] [v131072].nsp";

        var groups = TitleGrouper.Group([older, b, newer]);

        var g = Assert.Single(groups);
        Assert.Equal([131072, 65536], g.Updates.Select(m => m.Version).ToArray());
        Assert.Equal(newer, g.Updates[0].Path);
    }

    // ---- an orphan update with NO base → its own singleton, not attached, no invented base ----

    [Fact]
    public void Orphan_update_without_a_base_is_its_own_singleton()
    {
        var orphan = "Some Game [0100AAAABBBB0800].nsp";

        var groups = TitleGrouper.Group([orphan]);

        var g = Assert.Single(groups);
        Assert.Equal(orphan, g.BasePath);
        Assert.Equal("0100AAAABBBB0000", g.BaseTitleId); // its own id — not an invented base file
        Assert.Empty(g.Updates);
        Assert.Empty(g.Dlc);
    }

    // ---- an unidentifiable file → a singleton ----

    [Fact]
    public void Unidentifiable_file_is_a_singleton()
    {
        var iso = "Some Game.iso";

        var groups = TitleGrouper.Group([iso]);

        var g = Assert.Single(groups);
        Assert.Equal(iso, g.BasePath);
        Assert.Empty(g.Updates);
        Assert.Empty(g.Dlc);
    }

    // ---- PS3: a base pkg + an UPDATE.pkg sharing the game code → ONE group ----

    [Fact]
    public void Ps3_base_pkg_and_update_pkg_sharing_game_code_form_one_group()
    {
        var basePkg = WritePkg("Some Game (BLES01807).pkg", MakePkgBytes("UP0001-BLES01807_00-0000000000000000"));
        var updatePkg = WritePkg("Some Game (BLES01807) UPDATE.pkg", MakePkgBytes("UP0001-BLES01807_00-0000000000000000"));
        try
        {
            var groups = TitleGrouper.Group([updatePkg, basePkg]);

            var g = Assert.Single(groups);
            Assert.Equal("BLES01807", g.BaseTitleId);
            Assert.Equal(basePkg, g.BasePath);
            Assert.Equal(updatePkg, Assert.Single(g.Updates).Path);
            Assert.Empty(g.Dlc);
        }
        finally { File.Delete(basePkg); File.Delete(updatePkg); }
    }

    // ---- two base files sharing a title id → largest heads a group, the OTHER survives as a singleton ----

    [Fact]
    public void Duplicate_bases_sharing_an_id_keep_largest_and_surface_the_other_as_a_singleton()
    {
        // Same Wii U base program id, two dumps of different sizes.
        var big = WritePkg("Some Game [00050000101C9400] (dump A).wud", new byte[2000]);
        var small = WritePkg("Some Game [00050000101C9400] (dump B).wud", new byte[10]);

        var groups = TitleGrouper.Group([small, big]);

        // Neither dump silently vanishes: both appear on the shelf.
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal("00050000101C9400", g.BaseTitleId));

        var head = groups.Single(g => g.BasePath == big);      // largest heads its group
        var singleton = groups.Single(g => g.BasePath == small); // the loser is its own singleton
        Assert.Empty(singleton.Updates);
        Assert.Empty(singleton.Dlc);
        Assert.Empty(head.Updates);
        Assert.Empty(head.Dlc);
    }

    // ---- two distinct titles in one folder → two groups ----

    [Fact]
    public void Two_distinct_titles_form_two_groups()
    {
        var a = "Alpha [0100AAAABBBB0000].nsp";
        var c = "Charlie [0100CCCCDDDD0000].nsp";

        var groups = TitleGrouper.Group([c, a]);

        Assert.Equal(2, groups.Count);
        // deterministic ordering by BasePath (ordinal): "Alpha…" before "Charlie…"
        Assert.Equal(a, groups[0].BasePath);
        Assert.Equal(c, groups[1].BasePath);
    }

    // ---- helpers (same PKG byte template as Task 1) ----

    private static byte[] MakePkgBytes(string contentId)
    {
        var buf = new byte[0x30 + 36];
        buf[0] = 0x7F; buf[1] = (byte)'P'; buf[2] = (byte)'K'; buf[3] = (byte)'G';
        var cid = Encoding.ASCII.GetBytes(contentId);
        Array.Copy(cid, 0, buf, 0x30, Math.Min(cid.Length, 36));
        return buf;
    }

    private string WritePkg(string fileName, byte[] bytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ebs22-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
