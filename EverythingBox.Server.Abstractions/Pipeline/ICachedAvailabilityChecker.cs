namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Optional capability for debrid services that can report which torrents are
/// already cached (instantly available) <em>without</em> adding them. Implemented
/// by providers with a bulk availability endpoint (e.g. TorBox). Services that
/// lack one (e.g. Real-Debrid, which retired its endpoint) simply don't implement
/// this, and callers should treat availability as unknown.
/// </summary>
public interface ICachedAvailabilityChecker
{
    /// <summary>
    /// Given a set of BitTorrent info hashes, return the subset the service
    /// reports as already cached. Comparison is case-insensitive; unknown or
    /// failed lookups yield an empty set rather than throwing.
    /// </summary>
    Task<IReadOnlySet<string>> GetCachedHashesAsync(
        IEnumerable<string> infoHashes,
        CancellationToken cancellationToken = default);
}
