using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Providers.Torznab;

/// <summary>
/// Parses a Torznab RSS feed into <see cref="TorrentResult"/>s. Tolerant by
/// design: malformed XML yields an empty list, and an item missing fields is
/// skipped rather than throwing. Namespace-agnostic (matches on local element
/// names) so it copes with the slightly different feeds Prowlarr/Jackett emit.
/// </summary>
public static class TorznabFeedParser
{
    public static IReadOnlyList<TorrentResult> Parse(string xml, string providerName)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return [];
        }

        var results = new List<TorrentResult>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var parsed = ParseItem(item, providerName);
            if (parsed is not null)
                results.Add(parsed);
        }

        return results;
    }

    private static TorrentResult? ParseItem(XElement item, string providerName)
    {
        string? Child(string local) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;

        var attrs = item.Elements()
            .Where(e => e.Name.LocalName == "attr")
            .Select(e => (Name: e.Attribute("name")?.Value, Value: e.Attribute("value")?.Value))
            .Where(a => a.Name is not null && a.Value is not null)
            .ToList();

        string? Attr(string name) =>
            attrs.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
        IEnumerable<string> AttrAll(string name) =>
            attrs.Where(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).Select(a => a.Value!);

        var title = Child("title");
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var enclosure = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure");
        var enclosureUrl = enclosure?.Attribute("url")?.Value;
        var linkUrl = Child("link");

        var magnet = Attr("magneturl") ?? Pick(IsMagnet, enclosureUrl, linkUrl);
        var download = Pick(IsHttp, enclosureUrl, linkUrl);

        var size = ParseLong(Child("size") ?? Attr("size") ?? enclosure?.Attribute("length")?.Value);
        var seeders = ParseInt(Attr("seeders"));
        var leechers = ParseInt(Attr("leechers"));
        if (leechers is null && seeders is { } s && ParseInt(Attr("peers")) is { } peers)
            leechers = Math.Max(0, peers - s);

        DateTimeOffset? pubDate = DateTimeOffset.TryParse(
            Child("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

        return new TorrentResult
        {
            Title = title.Trim(),
            ProviderName = providerName,
            MagnetUri = TryUri(magnet),
            DownloadUrl = TryUri(download),
            InfoHash = Attr("infohash"),
            SizeBytes = size,
            Seeders = seeders,
            Leechers = leechers,
            PublishDate = pubDate,
            DetailsUrl = TryUri(Child("comments") ?? GuidPermalink(item)),
            Categories = AttrAll("category").ToList(),
        };
    }

    private static string? GuidPermalink(XElement item)
    {
        var guid = item.Elements().FirstOrDefault(e => e.Name.LocalName == "guid");
        if (guid is null)
            return null;

        var isPermalink = guid.Attribute("isPermaLink")?.Value;
        return string.Equals(isPermalink, "true", StringComparison.OrdinalIgnoreCase) && IsHttp(guid.Value)
            ? guid.Value
            : null;
    }

    private static string? Pick(Func<string?, bool> predicate, params string?[] candidates)
        => candidates.FirstOrDefault(predicate);

    private static bool IsMagnet(string? url) =>
        url is not null && url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttp(string? url) =>
        url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
