using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Plugins;

/// <summary>
/// The host capabilities handed to every plugin. Deliberately a thin holder — anything
/// with behaviour of its own belongs in the type behind the interface, not here.
/// </summary>
/// <param name="http">
/// The host's shared HttpClient (before <see cref="GrabberFactory"/>'s retry wrap — see
/// <see cref="CreateDebrid"/>), the same one Program.cs threads into every other pipeline
/// piece. Null only in tests that never call <see cref="CreateDebrid"/>.
/// </param>
/// <param name="loggerFactory">
/// Passed through to <see cref="GrabberFactory.CreateDebrid(string?, string?, HttpClient, ILoggerFactory, HttpMessageHandler?)"/>
/// exactly as Program.cs passes it to <see cref="GrabberFactory.BuildDebrid(ServerConfig, HttpClient, ILoggerFactory, HttpMessageHandler?)"/>
/// for the config-driven debrid, so a plugin-requested one is logged and retried the same way.
/// </param>
/// <param name="transport">
/// The shared transport <paramref name="http"/> is layered over — see
/// <see cref="GrabberFactory.BuildDebrid(ServerConfig, HttpClient, ILoggerFactory, HttpMessageHandler?)"/>'s
/// own <c>transport</c> parameter for why this needs to be the SAME handler rather than none.
/// </param>
public sealed class ServerServices(
    ITorrentGrabber grabber,
    IDebridService? debrid,
    IFileCache files,
    HttpClient? http = null,
    ILoggerFactory? loggerFactory = null,
    HttpMessageHandler? transport = null) : IServerServices
{
    public ITorrentGrabber Grabber { get; } = grabber;
    public IDebridService? Debrid { get; } = debrid;
    public IFileCache Files { get; } = files;

    /// <inheritdoc/>
    public IDebridService? CreateDebrid(string provider, string apiKey)
    {
        if (http is null || loggerFactory is null)
            throw new InvalidOperationException(
                "This ServerServices instance was not given an HttpClient/ILoggerFactory, so it cannot build a debrid service.");

        return GrabberFactory.CreateDebrid(provider, apiKey, http, loggerFactory, transport);
    }
}
