namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A request for an audiobook. Covers the gap left by deprecated tools such as
/// Readarr's audiobook support.
/// </summary>
public sealed class AudiobookRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Audiobook;

    public string? Author { get; init; }
    public string? Narrator { get; init; }
}
