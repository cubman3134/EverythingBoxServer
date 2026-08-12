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
        => new(roots.Length == 0 ? [_root] : roots, NullLogger<RomLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    [Fact]
    public void No_roots_configured_has_no_catalogs()
        => Assert.Empty(new RomLibrarySource([], NullLogger<RomLibrarySource>.Instance).Catalogs);

    [Fact]
    public void A_configured_root_advertises_the_games_catalog()
    {
        var c = Assert.Single(Roms().Catalogs);
        Assert.Equal("games", c.Id);
        Assert.Equal("game", c.MediaType);
    }

    [Fact]
    public async Task Each_system_subfolder_becomes_a_platform_item()
    {
        Directory.CreateDirectory(Path.Combine(_root, "snes"));
        Directory.CreateDirectory(Path.Combine(_root, "psx"));

        var catalog = await Roms().SearchAsync("games", null, Ctx(), default);
        Assert.Equal(2, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.Equal("platform", i.MediaType));
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
        Assert.Equal("platform", item.MediaType);
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
        Assert.All(catalog.Items, i => Assert.Equal("game", i.MediaType));
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
}
