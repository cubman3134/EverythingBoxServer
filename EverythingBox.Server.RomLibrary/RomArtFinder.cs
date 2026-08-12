namespace EverythingBox.Server.RomLibrary;

/// <summary>Locates a boxart image for a ROM by ES-DE / RetroBat sibling-file conventions, when the
/// gamelist does not point at one. Returns an absolute path or null; the caller containment-checks it.</summary>
internal static class RomArtFinder
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    // Subfolders (relative to the system folder) that conventionally hold per-rom art, name == rom stem.
    private static readonly string[] ArtSubfolders = ["images", Path.Combine("media", "covers"), Path.Combine("media", "images")];
    private static readonly string[] FolderBaseNames = ["boxart", "folder"];

    public static string? BoxartFor(string romPath)
    {
        var dir = Path.GetDirectoryName(romPath);
        if (dir is null) return null;
        var stem = Path.GetFileNameWithoutExtension(romPath);

        // 1) "<stem>-image.<img>" next to the ROM.
        foreach (var ext in ImageExtensions)
        {
            var p = Path.Combine(dir, stem + "-image" + ext);
            if (File.Exists(p)) return p;
        }
        // 2) "<artsubfolder>/<stem>.<img>".
        foreach (var sub in ArtSubfolders)
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, sub, stem + ext);
                if (File.Exists(p)) return p;
            }
        // 3) folder-level "boxart.*" / "folder.*".
        foreach (var baseName in FolderBaseNames)
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, baseName + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
