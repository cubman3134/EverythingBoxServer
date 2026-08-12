using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace EverythingBox.Server.RomLibrary;

/// <summary>One game's fields read from a gamelist.xml entry. Image is the RAW relative path as written
/// in the gamelist (resolved + containment-checked by the caller, never trusted here).</summary>
internal sealed record GameEntry(
    string? Name, string? Desc, int? Year,
    string? Developer, string? Publisher, string? Genre, string? Players, string? ImageRelPath);

/// <summary>A parsed gamelist.xml, games indexed by the filename of their &lt;path&gt;.</summary>
internal sealed class GamelistIndex
{
    public static readonly GamelistIndex Empty = new(new Dictionary<string, GameEntry>());
    private readonly IReadOnlyDictionary<string, GameEntry> _byFileName;
    public GamelistIndex(IReadOnlyDictionary<string, GameEntry> byFileName) => _byFileName = byFileName;

    /// <summary>The entry for a ROM, matched by its file name (case-insensitive), or null.</summary>
    public GameEntry? ForRom(string romPath)
        => _byFileName.TryGetValue(Path.GetFileName(romPath), out var e) ? e : null;
}

/// <summary>
/// Reads a system folder's gamelist.xml (ES-DE / RetroBat) into an index keyed by the &lt;path&gt;
/// filename. XXE-safe (DTDs prohibited, no external resolver, entity expansion capped) exactly like
/// NfoReader; any failure (missing, malformed, disallowed DTD, I/O) → an empty index, never a throw.
/// </summary>
internal static class GamelistStore
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,   // 0 would mean UNLIMITED; backstop behind the prohibited DTD
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static GamelistIndex Load(string systemDir)
    {
        var gamelistPath = GamelistPath(systemDir);
        if (gamelistPath is null) return GamelistIndex.Empty;

        try
        {
            using var stream = File.OpenRead(gamelistPath);
            using var reader = XmlReader.Create(stream, Settings);
            var doc = XDocument.Load(reader);

            var map = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "game", StringComparison.OrdinalIgnoreCase)))
            {
                string? Child(string name) =>
                    g.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

                var path = Child("path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var fileName = Path.GetFileName(path.Replace('\\', '/'));   // gamelist paths are ./sub/rom.ext
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                var rd = Child("releasedate");           // yyyyMMddThhmmss
                int? year = rd is { Length: >= 4 } && int.TryParse(rd.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;

                var image = Child("image");
                if (string.IsNullOrWhiteSpace(image)) image = Child("thumbnail");

                // Last write wins if a gamelist lists a path twice — harmless, deterministic.
                map[fileName] = new GameEntry(
                    Name: Empty(Child("name")), Desc: Empty(Child("desc")), Year: year,
                    Developer: Empty(Child("developer")), Publisher: Empty(Child("publisher")),
                    Genre: Empty(Child("genre")), Players: Empty(Child("players")),
                    ImageRelPath: Empty(image));
            }
            return new GamelistIndex(map);
        }
        catch
        {
            return GamelistIndex.Empty;   // missing / malformed / disallowed DTD / I/O — all non-fatal
        }
    }

    /// <summary>The gamelist.xml for a system folder, or null. "gamelist.xml" then "miximages"-less common
    /// variants are not needed — ES-DE and RetroBat both use gamelist.xml in the system folder root.</summary>
    public static string? GamelistPath(string systemDir)
    {
        var p = Path.Combine(systemDir, "gamelist.xml");
        return File.Exists(p) ? p : null;
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
