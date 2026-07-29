namespace EverythingBox.Server.Abstractions;

/// <summary>A file this server built and now serves from <c>/files/{name}</c>.</summary>
public sealed record BuiltFile(string ServedName, string Path, string ContentType);

/// <summary>
/// Somewhere a source can put something it had to build before serving. Concurrent
/// requests for the same name produce one build, and a failed build is not cached.
/// </summary>
public interface IFileCache
{
    /// <summary>Directory the built files live in.</summary>
    string Root { get; }

    /// <summary><paramref name="servedName"/> must be a plain file name — it becomes both a
    /// path under <see cref="Root"/> and a URL segment.</summary>
    Task<BuiltFile?> GetOrBuildAsync(
        string servedName,
        Func<string, CancellationToken, Task<BuiltFile?>> build,
        CancellationToken cancellationToken);
}
