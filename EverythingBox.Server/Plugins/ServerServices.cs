using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Plugins;

/// <summary>
/// The host capabilities handed to every plugin. Deliberately a thin holder — anything
/// with behaviour of its own belongs in the type behind the interface, not here.
/// </summary>
public sealed class ServerServices(ITorrentGrabber grabber, IDebridService? debrid, IFileCache files) : IServerServices
{
    public ITorrentGrabber Grabber { get; } = grabber;
    public IDebridService? Debrid { get; } = debrid;
    public IFileCache Files { get; } = files;
}
