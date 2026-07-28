namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A source that owns its own catalogs, search and stream resolution.
/// Implement this when the torrent pipeline does not fit — a direct-download host,
/// a chapter-structured library, a local folder.
/// </summary>
public interface IMediaSource
{
    /// <summary>Namespaces every id this source emits. Must not contain ':'.</summary>
    string Key { get; }

    IReadOnlyList<CatalogDescriptor> Catalogs { get; }

    /// <summary>Presentation for any media type the client does not know natively.</summary>
    IReadOnlyList<MediaTypeDescriptor> MediaTypes => [];

    Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct);

    /// <summary>Expand one item — a series into episodes, a volume into chapters.</summary>
    Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct);

    /// <summary><paramref name="index"/> selects the N-th best source, so a user who
    /// rejects a result gets the next one without a re-search.</summary>
    Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct);

    /// <summary>Optional. Implement only when the client cannot fetch the URL itself.</summary>
    Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => Task.FromResult<ProxyResponse?>(null);

    /// <summary>Optional. The host calls this once for every registered source at startup,
    /// before it starts taking traffic, so a source can cache expensive state up front. This
    /// is a single best-effort attempt, not a retry loop and not a startup gate: a thrown
    /// exception or a <see cref="WarmUpStatus.Failed"/> result is logged as a warning and the
    /// server starts anyway. The host also bounds this call with a timeout, so a source that
    /// never returns is logged as a warning and abandoned rather than run to completion — no
    /// source can block another's warm-up or the server's availability by failing OR by
    /// hanging here.</summary>
    Task<WarmUpResult> WarmUpAsync(CancellationToken ct)
        => Task.FromResult(WarmUpResult.NotApplicable);
}
