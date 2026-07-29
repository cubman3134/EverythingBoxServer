using System.Collections.Concurrent;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

/// <summary>
/// Builds a served file once, even under concurrent requests, then serves it from disk.
/// A failed build is evicted so a retry can succeed.
/// </summary>
public sealed class FileCache : IFileCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<BuiltFile?>>> _builds = new(StringComparer.OrdinalIgnoreCase);

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

        // GetOrAdd's factory is NOT run under a lock — it can execute concurrently on many
        // threads, and only the dictionary insert is atomic. Wrapping the build in
        // Lazy<Task<...>>(ExecutionAndPublication) means several Lazy wrapper objects may be
        // constructed under contention, but only the one that wins the insert is ever
        // evaluated, so .Value — and therefore the build itself — runs exactly once.
        var lazy = _builds.GetOrAdd(servedName, name => new Lazy<Task<BuiltFile?>>(
            () => RunAsync(name, build, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var result = await lazy.Value;

            // A null result is a transient miss (the builder had nothing to build this time),
            // not a permanent one — eviction previously happened only in the catch block below,
            // so a legitimate null became cached forever and every later retry got null back
            // without the builder ever running again. Evict exactly like a failure does: only
            // this exact Lazy, only after the entry is certainly present.
            if (result is null)
                _builds.TryRemove(new KeyValuePair<string, Lazy<Task<BuiltFile?>>>(servedName, lazy));

            return result;
        }
        catch
        {
            // Evict only AFTER the entry is certainly present, and only this exact Lazy —
            // GetOrAdd runs its factory before inserting, so evicting from inside the build
            // races the insertion and leaves the faulted task cached forever. Matching on the
            // Lazy value means a concurrent successful rebuild is never removed by a late failure.
            _builds.TryRemove(new KeyValuePair<string, Lazy<Task<BuiltFile?>>>(servedName, lazy));
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
