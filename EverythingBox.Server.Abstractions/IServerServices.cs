namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Host capabilities a plugin can borrow rather than rebuild. Reached through
/// <see cref="IPluginContext.Server"/>.
/// </summary>
public interface IServerServices
{
    /// <summary>The pipeline, fed by every indexer registered across all plugins.</summary>
    ITorrentGrabber Grabber { get; }

    /// <summary>The configured debrid service, or null when the server has none.
    /// A source that needs one must handle null rather than assume.</summary>
    IDebridService? Debrid { get; }

    IFileCache Files { get; }
}
