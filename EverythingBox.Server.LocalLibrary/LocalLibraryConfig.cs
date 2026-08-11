namespace EverythingBox.Server.LocalLibrary;

public sealed class LocalLibraryConfig
{
    /// <summary>Absolute paths to folders whose video files are treated as movies.</summary>
    public List<string> Movies { get; set; } = [];

    /// <summary>Absolute paths to folders laid out as Show/Season NN/…; each immediate subfolder is a series.</summary>
    public List<string> Series { get; set; } = [];
}
