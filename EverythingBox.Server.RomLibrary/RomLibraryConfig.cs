namespace EverythingBox.Server.RomLibrary;

public sealed class RomLibraryConfig
{
    /// <summary>Absolute paths to ROM library roots. Each immediate subfolder of a root is a system
    /// (named by the console — snes/, psx/, megadrive/…); the files inside it are that system's games.</summary>
    public List<string> Roms { get; set; } = [];
}
