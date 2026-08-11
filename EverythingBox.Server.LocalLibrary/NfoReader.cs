using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace EverythingBox.Server.LocalLibrary;

internal sealed record NfoInfo(string? Title, int? Year, string? Plot);

/// <summary>
/// Reads &lt;title&gt;/&lt;year&gt;/&lt;plot&gt; from a Kodi .nfo (movie / tvshow / episodedetails roots all
/// carry them). Namespace-agnostic. XXE-safe: DTDs prohibited, no external resolver, entities capped.
/// Tolerant: any failure (missing, malformed, I/O, disallowed DTD) → null.
/// </summary>
internal static class NfoReader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static NfoInfo? TryRead(string nfoPath)
    {
        try
        {
            using var stream = File.OpenRead(nfoPath);
            using var reader = XmlReader.Create(stream, Settings);
            var doc = XDocument.Load(reader);

            string? First(string name) =>
                doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

            var title = First("title");
            var plot = First("plot");
            var year = int.TryParse(First("year"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : (int?)null;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(plot) && year is null)
                return null;

            return new NfoInfo(
                string.IsNullOrWhiteSpace(title) ? null : title,
                year,
                string.IsNullOrWhiteSpace(plot) ? null : plot);
        }
        catch
        {
            return null; // missing / malformed / disallowed DTD / I/O — all non-fatal
        }
    }
}
