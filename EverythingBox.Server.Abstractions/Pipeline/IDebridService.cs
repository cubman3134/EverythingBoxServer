namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A debrid service (Real-Debrid, AllDebrid, Premiumize, TorBox, …). Unlike an
/// <see cref="IDownloadClient"/>, which downloads on hardware you run, a debrid
/// service downloads the torrent on its own cloud and hands back direct,
/// unrestricted HTTP links you can fetch over plain HTTPS.
/// </summary>
public interface IDebridService
{
    /// <summary>Stable, human-readable name (stamped onto the result).</summary>
    string Name { get; }

    /// <summary>
    /// Resolve a torrent into direct links. Implementations should return a
    /// <see cref="DebridResult"/> (Resolved / Pending / Failed) rather than throw
    /// for expected outcomes — in particular an uncached torrent that the service
    /// accepted but hasn't finished caching should come back as
    /// <see cref="DebridStatus.Pending"/>.
    /// <para>
    /// When <paramref name="request"/> targets a specific episode or track and the
    /// torrent is a season pack / album, an implementation that supports per-file
    /// selection (e.g. Real-Debrid) may download only the matching file. Providers
    /// that can't pre-select simply ignore it and return all files.
    /// </para>
    /// </summary>
    Task<DebridResult> ResolveAsync(
        TorrentResult torrent,
        MediaRequest? request = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live caching status of a torrent (by info hash) on the service — how far its cloud download has got —
    /// or <c>null</c> if it isn't in the account / the service can't report progress. Lets the caller show
    /// "42% cached" for an uncached release still downloading. Optional: defaults to no progress.
    /// </summary>
    Task<DebridProgress?> GetProgressAsync(string infoHash, CancellationToken cancellationToken = default)
        => Task.FromResult<DebridProgress?>(null);
}
