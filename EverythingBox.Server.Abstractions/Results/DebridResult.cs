namespace EverythingBox.Server.Abstractions;

/// <summary>How a debrid resolution turned out.</summary>
public enum DebridStatus
{
    /// <summary>The torrent was available and direct links were produced.</summary>
    Resolved,

    /// <summary>Accepted by the service but not yet downloaded on their cloud (uncached).</summary>
    Pending,

    /// <summary>The resolution failed.</summary>
    Failed,
}

/// <summary>A single direct, unrestricted download produced by a debrid service.</summary>
public sealed record DebridLink(string FileName, Uri Url, long? SizeBytes);

/// <summary>
/// Live caching status of a torrent on a debrid service (how far its cloud download has got), so the
/// addon can tell the user "42% cached" while they wait for an uncached release. <paramref name="Progress"/>
/// is a 0–1 fraction; <paramref name="State"/> is the service's own state string (e.g. "downloading",
/// "stalled (no seeds)"); <paramref name="Seeds"/> is the swarm seed count when known.
/// </summary>
public sealed record DebridProgress(double Progress, string? State, int? Seeds)
{
    /// <summary>Progress as a 0–100 whole percent.</summary>
    public int Percent => (int)Math.Round(Math.Clamp(Progress, 0, 1) * 100);

    /// <summary>
    /// The download can't make progress (no seeds), per the service's own state — so it likely won't
    /// finish. Relies on the explicit state string ("stalled (no seeds)"): a freshly-added torrent legitimately
    /// reports 0 seeds for a moment while it connects to the swarm, so a bare seed count isn't enough.
    /// </summary>
    public bool Stalled => State is not null && State.Contains("stall", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The outcome of asking a debrid service to turn a torrent into direct links.
/// </summary>
public sealed record DebridResult(
    DebridStatus Status,
    string ServiceName,
    string? TorrentId,
    bool Cached,
    IReadOnlyList<DebridLink> Links,
    string? Message)
{
    /// <summary>True only when direct links were produced.</summary>
    public bool Success => Status == DebridStatus.Resolved;

    public static DebridResult Resolved(string service, string torrentId, bool cached, IReadOnlyList<DebridLink> links)
        => new(DebridStatus.Resolved, service, torrentId, cached, links, null);

    public static DebridResult Pending(string service, string torrentId, string statusMessage)
        => new(DebridStatus.Pending, service, torrentId, false, [], statusMessage);

    public static DebridResult Failed(string service, string message, string? torrentId = null)
        => new(DebridStatus.Failed, service, torrentId, false, [], message);
}
