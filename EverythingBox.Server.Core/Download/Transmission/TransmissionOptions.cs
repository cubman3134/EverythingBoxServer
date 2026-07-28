namespace EverythingBox.Server.Core.Download.Transmission;

/// <summary>Connection settings for a Transmission daemon's RPC interface.</summary>
public sealed class TransmissionOptions
{
    /// <summary>Base URL of the daemon, e.g. <c>http://localhost:9091</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>RPC path appended to <see cref="BaseUrl"/>. The Transmission default.</summary>
    public string RpcPath { get; init; } = "/transmission/rpc";

    /// <summary>RPC username, when the daemon requires HTTP Basic auth.</summary>
    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>Display name, stamped onto results.</summary>
    public string Name { get; init; } = "Transmission";

    /// <summary>Download directory used when a handoff doesn't specify a save path.</summary>
    public string? DefaultDownloadDir { get; init; }
}
