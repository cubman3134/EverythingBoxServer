namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Lists and extracts members of archive formats the BCL/<see cref="RemoteZip"/>
/// can't handle (7z, rar) — an upstream host that cannot extract those server-side
/// either. The core library stays dependency-free: a consumer supplies an
/// implementation (e.g. one backed by SharpCompress) and injects it into the file
/// resolver. Implementations typically download the whole archive (within a size
/// cap) and extract locally, so this is best for small archives where a single
/// member can't be pulled any other way.
/// </summary>
public interface INestedArchiveReader
{
    /// <summary>Whether this reader handles the given archive (by file name/extension).</summary>
    bool Supports(string archiveName);

    /// <summary>
    /// List the member files inside the archive at <paramref name="archiveUrl"/>.
    /// <paramref name="sizeBytes"/> is the archive's known size (from item metadata),
    /// so an over-cap archive can be skipped without downloading. Returns empty when
    /// the archive is too large or unreadable.
    /// </summary>
    Task<IReadOnlyList<NestedArchiveMember>> ListAsync(Uri archiveUrl, long? sizeBytes, CancellationToken cancellationToken = default);

    /// <summary>Extract a single member to <paramref name="destination"/>; returns bytes written.</summary>
    Task<long> ExtractAsync(Uri archiveUrl, string memberName, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>A member file inside a nested archive.</summary>
public sealed record NestedArchiveMember(string Name, long? Size);
