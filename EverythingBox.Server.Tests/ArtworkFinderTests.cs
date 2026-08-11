using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class ArtworkFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-art-" + Guid.NewGuid().ToString("N"));
    public ArtworkFinderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }
    private string Touch(string name) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, [0]); return p; }

    [Fact]
    public void Prefers_a_companion_poster_next_to_the_file()
    {
        var movie = Touch("The Matrix (1999).mkv");
        Touch("poster.jpg");
        var companion = Touch("The Matrix (1999)-poster.jpg");
        Assert.Equal(companion, ArtworkFinder.PosterFor(movie));
    }

    [Fact]
    public void Falls_back_to_folder_level_poster_then_folder_image()
    {
        var movie = Touch("Film.mkv");
        var folderPoster = Touch("poster.png");
        Assert.Equal(folderPoster, ArtworkFinder.PosterFor(movie));
    }

    [Fact]
    public void A_directory_uses_its_folder_poster()
    {
        var show = Path.Combine(_dir, "Show"); Directory.CreateDirectory(show);
        var p = Path.Combine(show, "folder.webp"); File.WriteAllBytes(p, [0]);
        Assert.Equal(p, ArtworkFinder.PosterFor(show));
    }

    [Fact]
    public void No_art_yields_null() => Assert.Null(ArtworkFinder.PosterFor(Touch("Lonely.mkv")));
}
