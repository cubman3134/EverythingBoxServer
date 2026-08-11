namespace EverythingBox.Server.LocalLibrary;

public sealed class LocalLibraryConfig
{
    /// <summary>Absolute paths to folders whose video files are treated as movies.</summary>
    public List<string> Movies { get; set; } = [];
}
