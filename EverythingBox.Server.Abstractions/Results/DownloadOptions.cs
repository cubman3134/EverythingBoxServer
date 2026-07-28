namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Per-handoff options applied when sending a release to a download client.
/// Anything left null falls back to the client's own configured default.
/// </summary>
public sealed class DownloadOptions
{
    /// <summary>Category/label to file the download under (e.g. "movies", "tv").</summary>
    public string? Category { get; init; }

    /// <summary>Destination directory on the client, overriding its default.</summary>
    public string? SavePath { get; init; }

    /// <summary>Add the torrent in a paused/stopped state instead of starting it.</summary>
    public bool Paused { get; init; }

    /// <summary>Tags to attach to the download, where the client supports them.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
