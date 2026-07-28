namespace EverythingBox.Server.Abstractions;

/// <summary>
/// The combined outcome of a grab-then-download: what the ranker chose and what
/// the download client did with it. <see cref="Download"/> is null when nothing
/// matched the request, so there was nothing to send.
/// </summary>
public sealed record DownloadResult(GrabResult Grab, AddTorrentResult? Download)
{
    /// <summary>A release was found and selected.</summary>
    public bool Found => Grab.Found;

    /// <summary>The selected release was successfully handed to the client.</summary>
    public bool Sent => Download is { Success: true };
}
