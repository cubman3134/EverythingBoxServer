using EverythingBox.Server.Abstractions;
using EverythingBox.Server.RomLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class RomLibrarySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-rom-" + Guid.NewGuid().ToString("N"));

    public RomLibrarySourceTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } GC.SuppressFinalize(this); }

    private RomLibrarySource Roms(params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, true, null, NullLogger<RomLibrarySource>.Instance);

    // Grouping toggled explicitly (Increment 2): true collapses a title's base+update+DLC, false lists flat.
    private RomLibrarySource RomsGrouped(bool group)
        => new([_root], group, null, NullLogger<RomLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    [Fact]
    public void No_roots_configured_has_no_catalogs()
        => Assert.Empty(new RomLibrarySource([], true, null, NullLogger<RomLibrarySource>.Instance).Catalogs);

    [Fact]
    public void A_configured_root_advertises_the_games_catalog()
    {
        var c = Assert.Single(Roms().Catalogs);
        Assert.Equal("games", c.Id);
        Assert.Equal("game", c.Kind);
    }

    [Fact]
    public async Task Each_system_subfolder_becomes_a_platform_item()
    {
        Directory.CreateDirectory(Path.Combine(_root, "snes"));
        Directory.CreateDirectory(Path.Combine(_root, "psx"));

        var catalog = await Roms().SearchAsync("games", null, Ctx(), default);
        Assert.Equal(2, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.Equal("platform", i.Kind));
        Assert.All(catalog.Items, i => Assert.True(i.Expandable));
        Assert.Contains(catalog.Items, i => i.Title == RomSystems.Resolve("snes")!.Value.Title);
        Assert.Contains(catalog.Items, i => i.Title == RomSystems.Resolve("psx")!.Value.Title);
        Assert.All(catalog.Items, i => Assert.NotNull(SafeLocalFileServer.TryDecodeId(i.Id)));
    }

    [Fact]
    public async Task An_unrecognized_folder_is_titled_by_its_folder_name()
    {
        Directory.CreateDirectory(Path.Combine(_root, "weirdbox"));

        var item = Assert.Single((await Roms().SearchAsync("games", null, Ctx(), default)).Items);
        Assert.Equal("weirdbox", item.Title);
        Assert.Equal("platform", item.Kind);
    }

    [Fact]
    public async Task A_non_games_catalog_is_empty()
    {
        Directory.CreateDirectory(Path.Combine(_root, "snes"));
        Assert.Empty((await Roms().SearchAsync("nope", null, Ctx(), default)).Items);
    }

    // ---- Task 2: drill a platform into its ROMs, resolve/serve the stream, containment ----

    private string MakeSnesWithGames()
    {
        var snes = Path.Combine(_root, "snes");
        Directory.CreateDirectory(snes);
        File.WriteAllBytes(Path.Combine(snes, "Game A.sfc"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(snes, "Game B.sfc"), new byte[] { 5, 6, 7, 8 });
        File.WriteAllText(Path.Combine(snes, "notes.txt"), "junk");
        File.WriteAllText(Path.Combine(snes, ".DS_Store"), "dotfile");
        return snes;
    }

    [Fact]
    public async Task A_platform_expands_into_its_rom_files()
    {
        var snes = MakeSnesWithGames();
        var platformId = SafeLocalFileServer.EncodeId(snes);

        var catalog = await Roms().DetailAsync(platformId, Ctx(), default);

        Assert.Equal(2, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.Equal("game", i.Kind));
        Assert.All(catalog.Items, i => Assert.False(i.Expandable));
        Assert.Equal(new[] { "Game A", "Game B" }, catalog.Items.Select(i => i.Title).ToArray());
        Assert.DoesNotContain(catalog.Items, i => i.Title.Contains("notes"));
        Assert.DoesNotContain(catalog.Items, i => i.Subtitle == ".DS_Store");
    }

    [Fact]
    public async Task Detail_on_a_foreign_directory_id_is_empty()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-rom-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Empty((await Roms().DetailAsync(evilId, Ctx(), default)).Items);
        }
        finally { Directory.Delete(outside, true); }
    }

    [Fact]
    public async Task Detail_on_a_game_file_id_is_empty()
    {
        var snes = MakeSnesWithGames();
        var fileId = SafeLocalFileServer.EncodeId(Path.Combine(snes, "Game A.sfc"));

        // A file is not a platform; ResolveSafeDir's Directory.Exists gate rejects it.
        Assert.Empty((await Roms().DetailAsync(fileId, Ctx(), default)).Items);
    }

    [Fact]
    public async Task Resolve_on_a_game_id_returns_a_proxy_stream()
    {
        var snes = MakeSnesWithGames();
        var romId = SafeLocalFileServer.EncodeId(Path.Combine(snes, "Game A.sfc"));

        var stream = await Roms().ResolveAsync(romId, 0, Ctx(), default);

        Assert.NotNull(stream);
        Assert.StartsWith("proxy/romlib/", stream!.Url);
        Assert.EndsWith(Uri.EscapeDataString("Game A.sfc"), stream.Url);
        Assert.Equal("application/x-sfc", stream.Mime);
    }

    [Fact]
    public async Task Resolve_on_a_foreign_id_is_null()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-rom-outside-" + Guid.NewGuid().ToString("N") + ".sfc");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Null(await Roms().ResolveAsync(evilId, 0, Ctx(), default));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Open_serves_the_rom_with_range()
    {
        var snes = MakeSnesWithGames();
        var romId = SafeLocalFileServer.EncodeId(Path.Combine(snes, "Game A.sfc"));

        await using (var partial = await Roms().OpenAsync(romId, "bytes=0-3", default))
        {
            Assert.NotNull(partial);
            Assert.Equal(206, partial!.StatusCode);
            using var sink = new MemoryStream();
            await partial.Body.CopyToAsync(sink);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.ToArray());
        }

        await using var full = await Roms().OpenAsync(romId, null, default);
        Assert.NotNull(full);
        Assert.Equal(200, full!.StatusCode);
        using var fullSink = new MemoryStream();
        await full.Body.CopyToAsync(fullSink);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, fullSink.ToArray());
    }

    [Fact]
    public async Task Open_on_a_traversal_id_serves_nothing()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-rom-outside-" + Guid.NewGuid().ToString("N") + ".sfc");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            var evilId = SafeLocalFileServer.EncodeId(outside);
            Assert.Null(await Roms().OpenAsync(evilId, null, default));
        }
        finally { File.Delete(outside); }
    }

    // ---- Task 3: gamelist titles, boxart, a meta panel, and traversal safety ----

    // A snes folder with: "Game A.sfc" described by a gamelist.xml that names it and points at a boxart;
    // "Game B.sfc" absent from the gamelist but with a sibling "<stem>-image.png".
    private string MakeSnesWithGamelist()
    {
        var snes = Path.Combine(_root, "snes");
        Directory.CreateDirectory(snes);
        File.WriteAllBytes(Path.Combine(snes, "Game A.sfc"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(snes, "Game B.sfc"), new byte[] { 5, 6, 7, 8 });
        File.WriteAllBytes(Path.Combine(snes, "Game B-image.png"), new byte[] { 9, 9, 9 });
        Directory.CreateDirectory(Path.Combine(snes, "boxart"));
        File.WriteAllBytes(Path.Combine(snes, "boxart", "a.png"), new byte[] { 42, 42, 42, 42 });
        File.WriteAllText(Path.Combine(snes, "gamelist.xml"),
            "<?xml version=\"1.0\"?>\n" +
            "<gameList>\n" +
            "  <game>\n" +
            "    <path>./Game A.sfc</path>\n" +
            "    <name>Super Game A</name>\n" +
            "    <desc>A grand adventure.</desc>\n" +
            "    <genre>Platformer</genre>\n" +
            "    <releasedate>19911121T000000</releasedate>\n" +
            "    <developer>Acme</developer>\n" +
            "    <publisher>Publisher X</publisher>\n" +
            "    <players>1-2</players>\n" +
            "    <image>./boxart/a.png</image>\n" +
            "  </game>\n" +
            "</gameList>\n");
        return snes;
    }

    private static string IdOf(string proxyUrl) => proxyUrl.Split('/')[2];

    [Fact]
    public async Task Gamelist_name_and_boxart_appear_on_the_item()
    {
        var snes = MakeSnesWithGamelist();
        var platformId = SafeLocalFileServer.EncodeId(snes);

        var catalog = await Roms().DetailAsync(platformId, Ctx(), default);

        var a = Assert.Single(catalog.Items, i => i.Subtitle == "Game A.sfc");
        Assert.Equal("Super Game A", a.Title);
        Assert.NotNull(a.ThumbnailUrl);
        Assert.StartsWith("proxy/romlib/", a.ThumbnailUrl);
        Assert.EndsWith("/a.png", a.ThumbnailUrl);
        Assert.Equal(Path.GetFullPath(Path.Combine(snes, "boxart", "a.png")),
            SafeLocalFileServer.TryDecodeId(IdOf(a.ThumbnailUrl!)));
    }

    [Fact]
    public async Task Meta_panel_carries_overview_boxart_and_facts()
    {
        var snes = MakeSnesWithGamelist();
        var romId = SafeLocalFileServer.EncodeId(Path.Combine(snes, "Game A.sfc"));

        var detail = await Roms().MetaAsync(romId, Ctx(), default);

        Assert.NotNull(detail);
        Assert.Equal("Super Game A", detail!.Title);
        Assert.Equal("A grand adventure.", detail.Overview);
        Assert.NotNull(detail.ImageUrl);
        Assert.EndsWith("/a.png", detail.ImageUrl);
        Assert.NotNull(detail.Facts);
        Assert.Contains(detail.Facts!, f => f.Label == "Year" && f.Value == "1991");
        Assert.Contains(detail.Facts!, f => f.Label == "Genre" && f.Value == "Platformer");
    }

    [Fact]
    public async Task A_game_absent_from_the_gamelist_uses_its_stem_and_sibling_art()
    {
        var snes = MakeSnesWithGamelist();
        var platformId = SafeLocalFileServer.EncodeId(snes);

        var catalog = await Roms().DetailAsync(platformId, Ctx(), default);

        var b = Assert.Single(catalog.Items, i => i.Subtitle == "Game B.sfc");
        Assert.Equal("Game B", b.Title);
        Assert.NotNull(b.ThumbnailUrl);
        Assert.Equal(Path.GetFullPath(Path.Combine(snes, "Game B-image.png")),
            SafeLocalFileServer.TryDecodeId(IdOf(b.ThumbnailUrl!)));
    }

    [Fact]
    public async Task A_traversal_image_in_the_gamelist_yields_no_art()
    {
        var nes = Path.Combine(_root, "nes");
        Directory.CreateDirectory(nes);
        File.WriteAllBytes(Path.Combine(nes, "Evil.nes"), new byte[] { 1 });
        // An EXISTING file OUTSIDE the roots, two levels up from the system folder.
        var secret = Path.GetFullPath(Path.Combine(nes, "..", "..", "secret.png"));
        File.WriteAllBytes(secret, new byte[] { 7, 7, 7 });
        try
        {
            File.WriteAllText(Path.Combine(nes, "gamelist.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<gameList>\n" +
                "  <game>\n" +
                "    <path>./Evil.nes</path>\n" +
                "    <name>Evil Game</name>\n" +
                "    <image>../../secret.png</image>\n" +
                "  </game>\n" +
                "</gameList>\n");

            var platformId = SafeLocalFileServer.EncodeId(nes);
            var catalog = await Roms().DetailAsync(platformId, Ctx(), default);

            var item = Assert.Single(catalog.Items);
            Assert.Equal("Evil Game", item.Title);
            Assert.Null(item.ThumbnailUrl); // the outside file is never addressed

            var romId = SafeLocalFileServer.EncodeId(Path.Combine(nes, "Evil.nes"));
            var detail = await Roms().MetaAsync(romId, Ctx(), default);
            Assert.NotNull(detail);
            Assert.Null(detail!.ImageUrl);
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task Boxart_serves_with_200()
    {
        var snes = MakeSnesWithGamelist();
        var boxId = SafeLocalFileServer.EncodeId(Path.Combine(snes, "boxart", "a.png"));

        await using var resp = await Roms().OpenAsync(boxId, null, default);
        Assert.NotNull(resp);
        Assert.Equal(200, resp!.StatusCode);
        using var sink = new MemoryStream();
        await resp.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 42, 42, 42, 42 }, sink.ToArray());
    }

    // ---- Increment 2: group a title's base + update + DLC into one expandable game; drill to members ----

    // A switch folder holding one title (base [..0000] + an update [..0800] v65536 + a DLC [..1000], all
    // sharing base id 0100AAAABBBB0000) plus an unrelated plain game with no title id. Grouping is by the
    // filename's title id, so tiny byte content is enough — no real ROM.
    private (string Dir, string BasePath, string UpdatePath, string DlcPath, string OtherPath) MakeSwitchTitle()
    {
        var dir = Path.Combine(_root, "switch");
        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, "[0100AAAABBBB0000].nsp");
        var updatePath = Path.Combine(dir, "[0100AAAABBBB0800][v65536].nsp");
        var dlcPath = Path.Combine(dir, "[0100AAAABBBB1000].nsp");
        var otherPath = Path.Combine(dir, "Other Game.nsp");
        File.WriteAllBytes(basePath, new byte[] { 1 });
        File.WriteAllBytes(updatePath, new byte[] { 2 });
        File.WriteAllBytes(dlcPath, new byte[] { 3 });
        File.WriteAllBytes(otherPath, new byte[] { 4 });
        return (dir, basePath, updatePath, dlcPath, otherPath);
    }

    [Fact]
    public async Task Grouped_platform_collapses_a_title_into_one_expandable_base()
    {
        var t = MakeSwitchTitle();
        var platformId = SafeLocalFileServer.EncodeId(t.Dir);

        var catalog = await RomsGrouped(true).DetailAsync(platformId, Ctx(), default);

        // TWO items: the grouped base + the plain game — NOT four flat leaves.
        Assert.Equal(2, catalog.Items.Count);

        var grouped = Assert.Single(catalog.Items, i => i.Id == SafeLocalFileServer.EncodeId(t.BasePath));
        Assert.True(grouped.Expandable);
        Assert.Equal("1 update · 1 DLC", grouped.Subtitle);

        var plain = Assert.Single(catalog.Items, i => i.Id == SafeLocalFileServer.EncodeId(t.OtherPath));
        Assert.False(plain.Expandable);
        Assert.Equal("Other Game.nsp", plain.Subtitle);

        // The update and DLC files are NOT separate top-level leaves.
        Assert.DoesNotContain(catalog.Items, i => i.Id == SafeLocalFileServer.EncodeId(t.UpdatePath));
        Assert.DoesNotContain(catalog.Items, i => i.Id == SafeLocalFileServer.EncodeId(t.DlcPath));
    }

    [Fact]
    public async Task Drilling_a_grouped_base_lists_its_members_each_streamable()
    {
        var t = MakeSwitchTitle();
        var baseId = SafeLocalFileServer.EncodeId(t.BasePath);
        var source = RomsGrouped(true);

        var catalog = await source.DetailAsync(baseId, Ctx(), default);

        Assert.Equal(3, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.Equal("game", i.Kind));
        Assert.All(catalog.Items, i => Assert.False(i.Expandable));
        Assert.Equal(new[] { "Base game", "Update v65536", "DLC" },
            catalog.Items.Select(i => i.Title).ToArray());

        // Each member id resolves to its own file's proxy stream.
        foreach (var item in catalog.Items)
        {
            var stream = await source.ResolveAsync(item.Id, 0, Ctx(), default);
            Assert.NotNull(stream);
            Assert.StartsWith("proxy/romlib/", stream!.Url);
        }
        // The member ids are the three distinct files.
        Assert.Equal(
            new[] { t.BasePath, t.UpdatePath, t.DlcPath }.Select(SafeLocalFileServer.EncodeId).ToHashSet(),
            catalog.Items.Select(i => i.Id).ToHashSet());
    }

    [Fact]
    public async Task Drilling_a_member_or_plain_game_is_empty()
    {
        var t = MakeSwitchTitle();
        var source = RomsGrouped(true);

        // A DLC (a member of the base's group, not a base with members of its own) → nothing to expand.
        Assert.Empty((await source.DetailAsync(SafeLocalFileServer.EncodeId(t.DlcPath), Ctx(), default)).Items);
        // A plain game with no members → nothing to expand.
        Assert.Empty((await source.DetailAsync(SafeLocalFileServer.EncodeId(t.OtherPath), Ctx(), default)).Items);
    }

    [Fact]
    public async Task Grouping_off_lists_every_file_flat()
    {
        var t = MakeSwitchTitle();
        var platformId = SafeLocalFileServer.EncodeId(t.Dir);

        var catalog = await RomsGrouped(false).DetailAsync(platformId, Ctx(), default);

        // All FOUR files list flat, none expandable, each subtitled by its own filename.
        Assert.Equal(4, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.False(i.Expandable));
        Assert.Equal(
            new[] { t.BasePath, t.UpdatePath, t.DlcPath, t.OtherPath }.Select(SafeLocalFileServer.EncodeId).ToHashSet(),
            catalog.Items.Select(i => i.Id).ToHashSet());
    }

    [Fact]
    public async Task A_base_with_no_update_or_dlc_is_a_non_expandable_leaf()
    {
        var dir = Path.Combine(_root, "switch");
        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, "[0100CCCCDDDD0000].nsp");
        File.WriteAllBytes(basePath, new byte[] { 1 });
        var platformId = SafeLocalFileServer.EncodeId(dir);

        var catalog = await RomsGrouped(true).DetailAsync(platformId, Ctx(), default);

        var only = Assert.Single(catalog.Items);
        Assert.False(only.Expandable);
        Assert.Equal("[0100CCCCDDDD0000].nsp", only.Subtitle);
        // A lone base has no members to drill.
        Assert.Empty((await RomsGrouped(true).DetailAsync(only.Id, Ctx(), default)).Items);
    }
}
