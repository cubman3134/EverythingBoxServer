namespace EverythingBox.Server.Abstractions;

/// <summary>Progress reported while downloading a torrent directly.</summary>
public readonly record struct TorrentDownloadProgress(long BytesDownloaded, long TotalBytes, double BytesPerSecond)
{
    public double? Fraction => TotalBytes > 0 ? Math.Min(1.0, (double)BytesDownloaded / TotalBytes) : null;
}

/// <summary>
/// Downloads a torrent directly via an in-process BitTorrent client, fetching only
/// the file(s) matching the request. A fallback for small, uncached releases when
/// you'd rather grab it yourself than wait on a debrid service to cache it.
/// </summary>
public interface ITorrentDownloader
{
    /// <summary>
    /// Download the requested file(s) from <paramref name="torrent"/> into
    /// <paramref name="directory"/>, returning the saved file paths. Returns an
    /// empty list when there's nothing to download (e.g. no magnet/info hash).
    /// </summary>
    Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent,
        MediaRequest? request,
        string directory,
        IProgress<TorrentDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
