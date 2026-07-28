using System.Text;

namespace EverythingBox.Server.Core.Providers;

/// <summary>Builds magnet URIs from an info hash, plus a shared default tracker set.</summary>
public static class MagnetBuilder
{
    public static readonly IReadOnlyList<string> DefaultTrackers =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.tracker.cl:1337/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://open.stealth.si:80/announce",
        "udp://exodus.desync.com:6969/announce",
    ];

    public static Uri Build(string infoHash, string displayName, IEnumerable<string> trackers)
    {
        var sb = new StringBuilder("magnet:?xt=urn:btih:")
            .Append(infoHash)
            .Append("&dn=")
            .Append(Uri.EscapeDataString(displayName));

        foreach (var tracker in trackers)
            sb.Append("&tr=").Append(Uri.EscapeDataString(tracker));

        return new Uri(sb.ToString());
    }
}
