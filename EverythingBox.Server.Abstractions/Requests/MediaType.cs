namespace EverythingBox.Server.Abstractions;

/// <summary>
/// The broad category of media a request targets. Providers advertise which of
/// these they can serve via <see cref="ProviderCapabilities"/>, and
/// the grabber only routes a request to providers that support its type.
/// </summary>
public enum MediaType
{
    Movie,
    Tv,
    Music,
    Audiobook,
    Book,
    Comic,
    Other,

    /// <summary>PC game releases, located through a JSON feed of downloadable items.</summary>
    PcGame,
}
