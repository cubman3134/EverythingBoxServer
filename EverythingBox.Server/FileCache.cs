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

    public Task<BuiltFile?> GetOrBuildAsync(
        string servedName,
        Func<string, CancellationToken, Task<BuiltFile?>> build,
        CancellationToken ct)
    {
        ValidateName(servedName);

        return _builds.GetOrAdd(servedName, name => RunAsync(name, build, ct));
    }

    private async Task<BuiltFile?> RunAsync(
        string name, Func<string, CancellationToken, Task<BuiltFile?>> build, CancellationToken ct)
    {
        try
        {
            return await build(name, ct);
        }
        catch
        {
            _builds.TryRemove(name, out _);   // never cache a failure
            throw;
        }
    }

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
