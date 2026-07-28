namespace EverythingBox.Server.Abstractions;

/// <summary>A request for a single movie.</summary>
public sealed class MovieRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.Movie;

    /// <summary>Preferred edition, e.g. "Director's Cut", "Extended", "IMAX".</summary>
    public string? Edition { get; init; }
}
