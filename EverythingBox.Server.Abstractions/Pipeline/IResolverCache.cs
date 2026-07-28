namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A small string key/value cache the file resolver uses
/// to avoid re-hitting a slow upstream host for things that rarely
/// change within a session — search results, item metadata, and ZIP directory
/// listings. The core library stays free of filesystem policy: a consumer supplies an
/// implementation (e.g. a size-bounded on-disk cache) and injects it into the resolver.
/// Implementations should be resilient — a miss, an expired entry, or any error
/// returns null from <see cref="GetAsync"/>, and <see cref="SetAsync"/> is best-effort.
/// </summary>
public interface IResolverCache
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
}
