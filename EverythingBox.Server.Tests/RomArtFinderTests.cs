using EverythingBox.Server.RomLibrary;

namespace EverythingBox.Server.Tests;

public class RomArtFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-romart-" + Guid.NewGuid().ToString("N"));
    public RomArtFinderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private string Touch(string relPath)
    {
        var p = Path.Combine(_dir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "img");
        return p;
    }

    private string Rom(string name = "game.nes") => Path.Combine(_dir, name);

    [Fact]
    public void Sibling_stem_image_beats_images_subfolder()
    {
        var sibling = Touch("game-image.png");
        Touch(Path.Combine("images", "game.png"));   // lower priority
        Assert.Equal(sibling, RomArtFinder.BoxartFor(Rom()));
    }

    [Fact]
    public void Images_subfolder_found_when_no_sibling()
    {
        var art = Touch(Path.Combine("images", "game.jpg"));
        Assert.Equal(art, RomArtFinder.BoxartFor(Rom()));
    }

    [Fact]
    public void Media_covers_subfolder_found()
    {
        var art = Touch(Path.Combine("media", "covers", "game.png"));
        Assert.Equal(art, RomArtFinder.BoxartFor(Rom()));
    }

    [Fact]
    public void Folder_boxart_is_last_resort()
    {
        var art = Touch("boxart.png");
        Assert.Equal(art, RomArtFinder.BoxartFor(Rom()));
    }

    [Fact]
    public void No_art_yields_null()
    {
        Assert.Null(RomArtFinder.BoxartFor(Rom()));
    }
}
