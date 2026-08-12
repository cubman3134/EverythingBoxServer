namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Bridges two media-type vocabularies. The <see cref="MediaType"/> enum is the
/// <b>pipeline</b> vocabulary (what a source's code works in); the protocol strings on
/// this class are the <b>client</b> vocabulary (what appears on a
/// <c>CatalogDescriptor</c>/<c>CatalogItem</c>). This helper translates their
/// intersection so the many-to-one and no-mapping cases are decided once here instead of
/// reinvented at each call site.
///
/// The two vocabularies do not fully overlap:
/// <list type="bullet">
///   <item><c>Music</c> is in both — same name on each side.</item>
///   <item><c>Platform</c> is client-only — a container type with no enum member, so
///     <see cref="TryParseProtocol"/> deliberately does not recognise it.</item>
///   <item><c>Other</c> is pipeline-only — a catch-all with no protocol string, so
///     <see cref="ToProtocolString"/> returns null for it.</item>
///   <item><c>manga</c> and <c>comic</c> both parse to the single <c>Comic</c> member;
///     the reverse direction emits <c>comic</c>.</item>
/// </list>
/// </summary>
public static class MediaTypeNames
{
    // The canonical client protocol-string vocabulary — one home for every value that appears on a
    // CatalogDescriptor/CatalogItem. Use these instead of string literals at call sites.
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Comic = "comic";
    public const string Manga = "manga";
    public const string Book = "book";
    public const string Audiobook = "audiobook";
    public const string Music = "music";
    public const string Game = "game";
    public const string Platform = "platform";

    private static readonly Dictionary<MediaType, string> ToProtocol = new()
    {
        [MediaType.Movie] = Movie,
        [MediaType.Tv] = Series,
        [MediaType.Music] = Music,
        [MediaType.Audiobook] = Audiobook,
        [MediaType.Book] = Book,
        [MediaType.Comic] = Comic,
        [MediaType.PcGame] = Game,
        // MediaType.Other is deliberately absent — a pipeline-side catch-all with
        // nothing meaningful to show a client.
    };

    private static readonly Dictionary<string, MediaType> FromProtocol =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Movie] = MediaType.Movie,
            [Series] = MediaType.Tv,
            [Music] = MediaType.Music,
            [Audiobook] = MediaType.Audiobook,
            [Book] = MediaType.Book,
            [Comic] = MediaType.Comic,
            [Manga] = MediaType.Comic,   // same pipeline handling; only the shelf differs
            [Game] = MediaType.PcGame,
            // Platform is deliberately absent — a client-only container type with no enum member.
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
