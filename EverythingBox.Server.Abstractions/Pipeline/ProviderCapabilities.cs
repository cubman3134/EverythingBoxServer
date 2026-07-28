namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Declares what a provider can do, so the grabber can route requests correctly
/// and so callers can reason about a provider without invoking it.
/// </summary>
public sealed class ProviderCapabilities
{
    /// <summary>Media types this provider can search.</summary>
    public required IReadOnlySet<MediaType> SupportedMediaTypes { get; init; }

    /// <summary>True if the provider needs credentials/cookies configured to work.</summary>
    public bool RequiresAuthentication { get; init; }

    /// <summary>True if results normally carry a magnet link.</summary>
    public bool ProvidesMagnet { get; init; } = true;

    /// <summary>True if results normally carry a downloadable <c>.torrent</c> URL.</summary>
    public bool ProvidesTorrentFile { get; init; }

    public bool Supports(MediaType type) => SupportedMediaTypes.Contains(type);
}
