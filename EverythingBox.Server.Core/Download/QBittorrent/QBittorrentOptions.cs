namespace EverythingBox.Server.Core.Download.QBittorrent;

/// <summary>Connection settings for a qBittorrent Web UI instance.</summary>
public sealed class QBittorrentOptions
{
    /// <summary>Base URL of the Web UI, e.g. <c>http://localhost:8080</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// Web UI username. Leave null/empty when "Bypass authentication for clients
    /// on localhost" is enabled — login is then skipped entirely.
    /// </summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>Display name, stamped onto results.</summary>
    public string Name { get; init; } = "qBittorrent";

    /// <summary>Category applied when a handoff doesn't specify one.</summary>
    public string? DefaultCategory { get; init; }

    /// <summary>Save path applied when a handoff doesn't specify one.</summary>
    public string? DefaultSavePath { get; init; }
}
