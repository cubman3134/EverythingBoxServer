namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A source of torrents. This is the core extension point: a "direct" provider
/// talks to one tracker/site over HTTP, while a future Torznab provider will
/// front Prowlarr/Jackett — both are just implementations of this interface.
/// </summary>
public interface ITorrentProvider
{
    /// <summary>Stable, human-readable name (also stamped onto results).</summary>
    string Name { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Search for releases matching <paramref name="request"/>. Implementations
    /// should return an empty list (not throw) for "no results", and should not
    /// be called for media types they do not support.
    /// </summary>
    Task<IReadOnlyList<TorrentResult>> SearchAsync(
        MediaRequest request,
        CancellationToken cancellationToken = default);
}
