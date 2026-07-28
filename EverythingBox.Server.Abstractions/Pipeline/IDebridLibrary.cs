namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Optional capability for debrid services that expose the user's own library of
/// added/cached torrents. Lets a caller search what's already in the account —
/// useful as a fallback when external indexers are unreachable, since these items
/// are by definition already on the service.
/// </summary>
public interface IDebridLibrary
{
    /// <summary>
    /// List the torrents currently in the user's account as results. Failures
    /// yield an empty list rather than throwing.
    /// </summary>
    Task<IReadOnlyList<TorrentResult>> ListLibraryAsync(CancellationToken cancellationToken = default);
}
