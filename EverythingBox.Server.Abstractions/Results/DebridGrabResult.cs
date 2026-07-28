namespace EverythingBox.Server.Abstractions;

/// <summary>
/// The combined outcome of a grab-then-debrid: what the ranker chose and what the
/// debrid service produced. <see cref="Debrid"/> is null when nothing matched the
/// request, so there was nothing to resolve.
/// </summary>
public sealed record DebridGrabResult(GrabResult Grab, DebridResult? Debrid)
{
    /// <summary>A release was found and selected.</summary>
    public bool Found => Grab.Found;

    /// <summary>The selected release was resolved into direct links.</summary>
    public bool Resolved => Debrid is { Success: true };
}
