namespace EverythingBox.Server.Abstractions;

/// <summary>
/// The search-and-rank pipeline, as a plugin sees it. Implemented in the Core library;
/// declared here because Abstractions is what plugins reference and it cannot depend on Core.
/// </summary>
public interface ITorrentGrabber
{
    /// <summary>Search, dedupe, parse, rank, and select the best release.</summary>
    Task<GrabResult> GrabAsync(MediaRequest request, CancellationToken cancellationToken = default);

    /// <summary>Search, dedupe and rank, returning every candidate rather than one pick.</summary>
    Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default);
}
