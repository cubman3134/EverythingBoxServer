namespace EverythingBox.Server.Abstractions;

/// <summary>
/// A request for a PC game by name. Matched against the titles in a configured
/// JSON release feed; a hit's magnet/file is then handed to the debrid or download
/// flow like any other release.
/// </summary>
public sealed class PcGameRequest : MediaRequest
{
    public override MediaType MediaType => MediaType.PcGame;
}
