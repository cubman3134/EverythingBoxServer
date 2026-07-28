namespace EverythingBox.Server.Abstractions;

/// <summary>What a provider did during one search — fed back to a tracker.</summary>
public sealed record ProviderOutcome(
    string ProviderName,
    int ResultCount,
    bool Errored,
    TimeSpan Elapsed,
    bool ProducedBest);

/// <summary>
/// Learns which providers give the best results and orders them so the strongest
/// are queried first. The grabber asks <see cref="Prioritize"/> before searching
/// and reports back via <see cref="Record"/> afterwards. Implementations are
/// expected to persist their stats so the ordering improves across runs.
/// </summary>
public interface IProviderPerformanceTracker
{
    /// <summary>Return the providers ordered best-first for querying.</summary>
    IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> providers);

    /// <summary>Record how each provider performed in a completed search.</summary>
    void Record(IReadOnlyList<ProviderOutcome> outcomes);
}
