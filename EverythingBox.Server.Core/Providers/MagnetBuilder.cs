using System.Text;

namespace EverythingBox.Server.Core.Providers;

/// <summary>Builds magnet URIs from an info hash.</summary>
public static class MagnetBuilder
{
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
