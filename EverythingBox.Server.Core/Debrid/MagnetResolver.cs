using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Torrents;

namespace EverythingBox.Server.Core.Debrid;

/// <summary>
/// Produces a magnet link for a result — from its magnet, its info hash, or by
/// fetching its <c>.torrent</c> file and reading the info hash out of it. Lets the
/// debrid path treat a <c>.torrent</c>-only result exactly like a magnet one.
/// </summary>
public static class MagnetResolver
{
    public static async Task<string?> ResolveAsync(HttpClient http, TorrentResult torrent, CancellationToken cancellationToken = default)
    {
        if (torrent.MagnetUri is not null)
            return torrent.MagnetUri.ToString();
        if (!string.IsNullOrWhiteSpace(torrent.InfoHash))
            return $"magnet:?xt=urn:btih:{torrent.InfoHash}";

        if (torrent.DownloadUrl is { } url && url.AbsoluteUri.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                if (TorrentInfo.TryReadInfoHash(bytes, out var hash))
                    return $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(torrent.Title)}";
            }
            catch (HttpRequestException)
            {
                // unreachable .torrent — fall through to null
            }
        }

        return null;
    }
}
