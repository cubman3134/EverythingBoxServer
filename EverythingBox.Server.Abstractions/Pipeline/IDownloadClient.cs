namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A torrent download client (qBittorrent, Transmission, Deluge, ...). The
/// pluggable counterpart to <see cref="ITorrentProvider"/>: providers find
/// releases, clients start downloading them.
/// </summary>
public interface IDownloadClient
{
    /// <summary>Stable, human-readable name (stamped onto the result).</summary>
    string Name { get; }

    /// <summary>
    /// Hand a release to the client to begin downloading. Implementations should
    /// return a failed <see cref="AddTorrentResult"/> rather than throw for
    /// expected problems (no link, auth failure, rejected by the client).
    /// </summary>
    Task<AddTorrentResult> AddAsync(
        TorrentResult torrent,
        DownloadOptions? options = null,
        CancellationToken cancellationToken = default);
}
