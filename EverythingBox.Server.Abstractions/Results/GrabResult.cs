namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A scored candidate, with the reasons that produced its score. <see cref="Cached"/>
/// is set when a debrid availability check found the release already cached.
/// </summary>
public sealed record ScoredTorrent(TorrentResult Result, double Score, IReadOnlyList<string> Reasons, bool Cached = false);

/// <summary>Something a single provider reported as having gone wrong.</summary>
public sealed record ProviderError(string ProviderName, string Message);

/// <summary>
/// The outcome of a grab: the auto-selected best release plus the full ranked
/// list (alternatives) and any per-provider errors that occurred along the way.
/// </summary>
public sealed class GrabResult
{
    /// <summary>The single best release the ranker chose, or null if none qualified.</summary>
    public TorrentResult? Best { get; init; }

    /// <summary>All eligible candidates, best first. <see cref="Best"/> is <c>Ranked[0]</c>.</summary>
    public IReadOnlyList<ScoredTorrent> Ranked { get; init; } = [];

    /// <summary>Providers that failed, and why. Empty when all succeeded.</summary>
    public IReadOnlyList<ProviderError> Errors { get; init; } = [];

    public bool Found => Best is not null;
}
