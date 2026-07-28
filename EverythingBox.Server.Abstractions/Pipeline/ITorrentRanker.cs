namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Filters out ineligible/irrelevant candidates and orders the rest, so the
/// grabber can auto-pick the single best release for a request.
/// </summary>
public interface ITorrentRanker
{
    /// <summary>Return eligible candidates ordered best-first.</summary>
    IReadOnlyList<ScoredTorrent> Rank(
        MediaRequest request,
        IEnumerable<TorrentResult> candidates,
        RankingOptions options);

    /// <summary>Convenience: the top result from <see cref="Rank"/>, or null.</summary>
    TorrentResult? SelectBest(
        MediaRequest request,
        IEnumerable<TorrentResult> candidates,
        RankingOptions options);
}
