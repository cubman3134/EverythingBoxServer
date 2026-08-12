namespace EverythingBox.Server.MusicLibrary;

public sealed class MusicLibraryConfig
{
    /// <summary>Absolute paths to music library roots (Artist/Album/… trees).</summary>
    public List<string> Roots { get; set; } = [];
}
