namespace EverythingBox.Server.Abstractions;

/// <summary>
/// The search-and-rank pipeline, as a plugin sees it. Implemented in the Core library;
/// declared here because Abstractions is what plugins reference and it cannot depend on Core.
/// </summary>
public interface ITorrentGrabber
{
    /// <summary>Search, dedupe, parse, rank, and select the best release.</summary>
    Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Search, dedupe, optionally parse, and return every candidate without ranking or filtering. See <see cref="GrabAsync"/> for the ranked, filtered, single-best path.</summary>
    Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default);
}
