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
    /// empty list when there's nothing to download (e.g. no magnet/info hash), or
    /// when the true combined size of the selected files exceeds
    /// <paramref name="maxTotalBytes"/> — a re-check of the caller's size cap against
    /// the real post-metadata size, which the indexer-reported size can under-state.
    /// </summary>
    /// <param name="torrent">The release to fetch, carrying whatever locator the indexer gave
    /// (magnet URI, info hash, or a <c>.torrent</c> link).</param>
    /// <param name="request">The media request used to pick which file(s) to fetch; null takes all.</param>
    /// <param name="directory">Where the selected file(s) are written.</param>
    /// <param name="progress">Optional progress sink reported while downloading.</param>
    /// <param name="maxTotalBytes">Refuse (return empty) if the selected files' real combined
    /// size exceeds this. Null means no cap.</param>
    /// <param name="cancellationToken">Cancels the download; a cancelled download returns empty.</param>
    Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent,
        MediaRequest? request,
        string directory,
        IProgress<TorrentDownloadProgress>? progress = null,
        long? maxTotalBytes = null,
        CancellationToken cancellationToken = default);
}
