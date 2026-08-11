namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A single candidate release returned by a provider. A record so the pipeline
/// can cheaply enrich it (e.g. attach <see cref="ParsedInfo"/>) with <c>with</c>
/// expressions without mutation.
/// </summary>
public sealed record TorrentResult
{
    /// <summary>The raw release title exactly as the provider reported it.</summary>
    public required string Title { get; init; }

    /// <summary>Name of the provider that produced this result.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Magnet link, when available.</summary>
    public Uri? MagnetUri { get; init; }

    /// <summary>Direct URL to a <c>.torrent</c> file, when available.</summary>
    public Uri? DownloadUrl { get; init; }

    /// <summary>BitTorrent info hash (hex), when known.</summary>
    public string? InfoHash { get; init; }

    public long? SizeBytes { get; init; }
    public int? Seeders { get; init; }
    public int? Leechers { get; init; }
    public DateTimeOffset? PublishDate { get; init; }

    /// <summary>Human-facing details/comments page on the source site.</summary>
    public Uri? DetailsUrl { get; init; }

    /// <summary>Provider-specific category labels for this release.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Metadata parsed from <see cref="Title"/>; null until the parser runs.</summary>
    public ReleaseInfo? ParsedInfo { get; init; }

    /// <summary>
    /// Explicit torrent member(s) to fetch — each entry a full in-torrent member path or a bare
    /// filename. When non-empty, the self-download path fetches exactly the matching members and
    /// ignores the request heuristic. Empty (the default) keeps request-driven matching. Only the
    /// self-download (MonoTorrent) path consults this.
    /// </summary>
    public IReadOnlyList<string> WantedMembers { get; init; } = [];

    /// <summary>
    /// Expected checksums for downloaded members. When a downloaded file matches an entry here
    /// (by filename), the self-download path verifies it and refuses to publish a mismatch.
    /// Empty (the default) skips verification entirely. Only the self-download (MonoTorrent)
    /// path consults this; debrid/direct paths are unaffected.
    /// </summary>
    public IReadOnlyList<MemberChecksum> ExpectedChecksums { get; init; } = [];

    /// <summary>True when there is some way to actually fetch this release.</summary>
    public bool IsDownloadable => MagnetUri is not null || DownloadUrl is not null;
}
