namespace EverythingBox.Server.Core.Debrid.TorBox;

/// <summary>Settings for the TorBox API.</summary>
public sealed class TorBoxOptions
{
    /// <summary>TorBox API key (from the TorBox dashboard → Settings → API).</summary>
    public required string ApiKey { get; init; }

    /// <summary>API base. Defaults to the public TorBox v1 endpoint.</summary>
    public Uri BaseUrl { get; init; } = new("https://api.torbox.app/v1/api/");

    /// <summary>Display name, stamped onto results.</summary>
    public string Name { get; init; } = "TorBox";

    /// <summary>
    /// How long to wait for an uncached torrent to finish on TorBox before
    /// returning <see cref="Abstractions.DebridStatus.Pending"/>. The default of
    /// <see cref="TimeSpan.Zero"/> means "cached-only".
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.Zero;

    /// <summary>Delay between status polls while waiting.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
}
