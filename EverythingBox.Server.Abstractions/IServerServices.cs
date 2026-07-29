namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Host capabilities a plugin can borrow rather than rebuild. Reached through
/// <see cref="IPluginContext.Server"/>.
/// </summary>
public interface IServerServices
{
    /// <summary>
    /// The pipeline, fed by every indexer registered across all plugins.
    /// <para>
    /// Do not call this from <see cref="IPlugin.Configure"/>. The host cannot build the real
    /// grabber until every plugin has finished registering its indexers, which is only true
    /// once all plugins' <c>Configure</c> methods have returned — so a call made from inside
    /// your own (or any other plugin's) <c>Configure</c> throws <see cref="InvalidOperationException"/>
    /// rather than silently returning a grabber with no indexers. Hold onto this reference
    /// during registration if you need it, and call it later, while serving a request.
    /// </para>
    /// </summary>
    ITorrentGrabber Grabber { get; }

    /// <summary>The configured debrid service, or null when the server has none.
    /// A source that needs one must handle null rather than assume.</summary>
    IDebridService? Debrid { get; }

    IFileCache Files { get; }
}
