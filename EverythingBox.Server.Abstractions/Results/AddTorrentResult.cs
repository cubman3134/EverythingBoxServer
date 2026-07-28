namespace EverythingBox.Server.Abstractions;

/// <summary>The outcome of handing a release to a download client.</summary>
public sealed record AddTorrentResult(bool Success, string ClientName, string? InfoHash, string? Message)
{
    public static AddTorrentResult Ok(string clientName, string? infoHash)
        => new(true, clientName, infoHash, null);

    public static AddTorrentResult Failed(string clientName, string message)
        => new(false, clientName, null, message);
}
