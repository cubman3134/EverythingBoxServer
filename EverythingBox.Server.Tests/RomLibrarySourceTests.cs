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
}
