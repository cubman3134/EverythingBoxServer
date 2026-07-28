using System.Collections.Concurrent;

namespace EverythingBox.Server;

public sealed record BuiltFile(string ServedName, string Path, string ContentType);

/// <summary>
/// Builds a served file once, even under concurrent requests, then serves it from disk.
/// A failed build is evicted so a retry can succeed.
/// </summary>
public sealed class FileCache
{
    private readonly ConcurrentDictionary<string, Task<BuiltFile?>> _builds = new(StringComparer.OrdinalIgnoreCase);

    public FileCache(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public async Task<BuiltFile?> GetOrBuildAsync(
        string servedName,
        Func<string, CancellationToken, Task<BuiltFile?>> build,
        CancellationToken ct)
    {
        ValidateName(servedName);

        var task = _builds.GetOrAdd(servedName, name => RunAsync(name, build, ct));
        try
        {
            return await task;
        }
        catch
        {
            // Evict only AFTER the entry is certainly present, and only this exact task —
            // GetOrAdd runs its factory before inserting, so evicting from inside the build
            // races the insertion and leaves the faulted task cached forever. Matching on the
            // task value means a concurrent successful rebuild is never removed by a late failure.
            _builds.TryRemove(new KeyValuePair<string, Task<BuiltFile?>>(servedName, task));
            throw;
        }
    }

    // async so that a builder throwing SYNCHRONOUSLY still surfaces as a faulted task
    // rather than propagating out of the factory inside GetOrAdd.
    private static async Task<BuiltFile?> RunAsync(
        string name, Func<string, CancellationToken, Task<BuiltFile?>> build, CancellationToken ct)
        => await build(name, ct);

    /// <summary>The name becomes a path under the cache root and a URL segment, so it
    /// must be a plain file name — no separators, no traversal.</summary>
    private static void ValidateName(string servedName)
    {
        if (string.IsNullOrWhiteSpace(servedName))
            throw new ArgumentException("A served file name must be non-empty.", nameof(servedName));

        if (servedName != Path.GetFileName(servedName))
            throw new ArgumentException($"'{servedName}' must be a plain file name.", nameof(servedName));
    }
}
