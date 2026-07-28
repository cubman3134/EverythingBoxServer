namespace EverythingBox.Server.Core.Debrid.RealDebrid;

/// <summary>Settings for the Real-Debrid REST API.</summary>
public sealed class RealDebridOptions
{
    /// <summary>Real-Debrid API token (from https://real-debrid.com/apitoken).</summary>
    public required string ApiToken { get; init; }

    /// <summary>API base. Defaults to the public Real-Debrid REST endpoint.</summary>
    public Uri BaseUrl { get; init; } = new("https://api.real-debrid.com/rest/1.0/");

    /// <summary>Display name, stamped onto results.</summary>
    public string Name { get; init; } = "Real-Debrid";

    /// <summary>
    /// How long to wait for an uncached torrent to finish caching on the service
    /// before returning <see cref="Abstractions.DebridStatus.Pending"/>. The default of
    /// <see cref="TimeSpan.Zero"/> means "cached-only": resolve instantly if the
    /// torrent is already available, otherwise return pending without waiting.
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.Zero;

    /// <summary>Delay between status polls while waiting for caching.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Unrestrict the per-file links into final direct downloads (default). When
    /// false, the raw restricted links are returned as-is.
    /// </summary>
    public bool UnrestrictLinks { get; init; } = true;
}
