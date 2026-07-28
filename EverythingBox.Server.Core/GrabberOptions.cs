using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core;

/// <summary>Top-level behavior knobs for <see cref="TorrentGrabber"/>.</summary>
public sealed class GrabberOptions
{
    /// <summary>How candidates are filtered and ordered when picking the best.</summary>
    public RankingOptions Ranking { get; init; } = RankingOptions.Default;

    /// <summary>
    /// Per-provider time budget. A provider that exceeds it is abandoned and
    /// recorded as a <see cref="ProviderError"/>, while the rest still
    /// contribute. A value of <see cref="TimeSpan.Zero"/> or less disables the
    /// timeout.
    /// </summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Query providers concurrently (default) instead of sequentially.</summary>
    public bool QueryProvidersInParallel { get; init; } = true;

    /// <summary>Merge duplicate releases across providers, keeping the most-seeded.</summary>
    public bool Deduplicate { get; init; } = true;

    /// <summary>Run the release parser over every result before ranking.</summary>
    public bool ParseReleases { get; init; } = true;

    /// <summary>
    /// "Quick grab" threshold. When set, the grabber stops querying providers as
    /// soon as a candidate reaches this ranker score and returns it immediately,
    /// rather than waiting for every provider. Null (default) means search all
    /// providers and pick the overall best.
    /// </summary>
    public double? QuickGrabScore { get; init; }

    /// <summary>
    /// When true and the configured debrid service can report cached availability
    /// (<see cref="ICachedAvailabilityChecker"/>), results already
    /// cached are marked and floated to the top, and a <see cref="QuickGrabScore"/>
    /// stop holds out for a cached hit (instant availability) rather than stopping
    /// on the first release that merely clears the score.
    /// </summary>
    public bool PreferCachedReleases { get; init; }
}
