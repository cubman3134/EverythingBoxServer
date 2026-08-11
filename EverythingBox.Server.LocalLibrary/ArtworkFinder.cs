namespace EverythingBox.Server.LocalLibrary;

/// <summary>Locates a poster image for a media file or a show folder, using Kodi/Jellyfin naming.</summary>
internal static class ArtworkFinder
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] FolderBaseNames = ["poster", "folder"];

    public static string? PosterFor(string mediaFileOrDir)
    {
        if (Directory.Exists(mediaFileOrDir))
            return FolderPoster(mediaFileOrDir);

        var dir = Path.GetDirectoryName(mediaFileOrDir);
        if (dir is null) return null;

        // 1) "<stem>-poster.<img>" next to the file.
        var stem = Path.GetFileNameWithoutExtension(mediaFileOrDir);
        foreach (var ext in ImageExtensions)
        {
            var companion = Path.Combine(dir, stem + "-poster" + ext);
            if (File.Exists(companion)) return companion;
        }
        // 2) "poster.*" / "folder.*" in the same directory.
        return FolderPoster(dir);
    }

    private static string? FolderPoster(string dir)
    {
        foreach (var baseName in FolderBaseNames)
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, baseName + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
