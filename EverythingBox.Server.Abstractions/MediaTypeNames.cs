namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Translates between the pipeline's <see cref="MediaType"/> enum and the addon
/// protocol's media-type strings. These are two different vocabularies with different
/// value sets — the enum has <c>Tv</c> where the protocol has <c>"series"</c>, and the
/// protocol has both <c>"comic"</c> and <c>"manga"</c> where the enum has one member.
/// Keeping the translation here means the many-to-one and no-mapping cases are decided
/// once instead of reinvented at each call site.
/// </summary>
public static class MediaTypeNames
{
    private static readonly Dictionary<MediaType, string> ToProtocol = new()
    {
        [MediaType.Movie] = "movie",
        [MediaType.Tv] = "series",
        [MediaType.Music] = "music",
        [MediaType.Audiobook] = "audiobook",
        [MediaType.Book] = "book",
        [MediaType.Comic] = "comic",
        [MediaType.PcGame] = "game",
        // MediaType.Other is deliberately absent — a pipeline-side catch-all with
        // nothing meaningful to show a client.
    };

    private static readonly Dictionary<string, MediaType> FromProtocol =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["movie"] = MediaType.Movie,
            ["series"] = MediaType.Tv,
            ["music"] = MediaType.Music,
            ["audiobook"] = MediaType.Audiobook,
            ["book"] = MediaType.Book,
            ["comic"] = MediaType.Comic,
            ["manga"] = MediaType.Comic,   // same pipeline handling; only the shelf differs
            ["game"] = MediaType.PcGame,
        };

    /// <summary>The protocol string for a media type, or null if it has none.</summary>
    public static string? ToProtocolString(MediaType type) =>
        ToProtocol.TryGetValue(type, out var protocol) ? protocol : null;

    /// <summary>Parses a protocol string. Case-insensitive; false for null, empty or unknown.</summary>
    public static bool TryParseProtocol(string? protocol, out MediaType type)
    {
        if (!string.IsNullOrEmpty(protocol)) return FromProtocol.TryGetValue(protocol, out type);
        type = default;
        return false;
    }
}
