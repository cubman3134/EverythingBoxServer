using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Abstractions;

/// <summary>A plugin's entry point. One public parameterless-constructible
/// implementation per plugin assembly.</summary>
public interface IPlugin
{
    /// <summary>Namespaces this plugin's config section. Must not contain ':'.</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>Set this to <c>new Version(ServerApi.VersionString)</c> — do NOT reference
    /// a host-resolved <see cref="Version"/> instance here. <see cref="ServerApi.VersionString"/>
    /// is a compile-time constant, so writing it this way bakes the version your plugin was
    /// built against into your plugin's own assembly. The host checks this against what it
    /// can support and refuses to load a plugin it cannot satisfy.</summary>
    Version ApiVersion { get; }

    void Configure(IPluginRegistry registry, IPluginContext context);
}

public interface IPluginRegistry
{
    void AddSource(IMediaSource source);

    /// <summary>
    /// Register an indexer. It inherits the whole pipeline — dedupe, release parsing,
    /// ranking, cached-first ordering and single-file extraction — without implementing
    /// any of it. This is the smaller of the two plugin tiers and usually the right one.
    /// </summary>
    void AddIndexer(ITorrentProvider provider);

    /// <summary>
    /// Register a metadata source. The host pairs it with the pipeline, so browsing and
    /// locating stay decoupled — any metadata source works with any set of indexers.
    /// </summary>
    void AddMetadata(IMetadataSource metadata);

    /// <summary>
    /// Supply a tracker that learns which indexers actually pay off. The grabber orders
    /// providers best-first before each search and reports back afterwards, so the
    /// strongest are queried first and a quick grab can finish sooner. At most one
    /// applies across the whole server.
    /// </summary>
    void AddProviderTracker(IProviderPerformanceTracker tracker);
}

public interface IPluginContext
{
    ILoggerFactory Loggers { get; }

    /// <summary>Shared client. The host owns its lifetime — do not dispose it.</summary>
    HttpClient Http { get; }

    /// <summary>Plugin-private directory, created by the host before Configure runs.</summary>
    string CacheDirectory { get; }

    /// <summary>This plugin's own section of the server config, or null if absent.</summary>
    T? GetConfig<T>() where T : class;

    /// <summary>Host capabilities a plugin can borrow instead of rebuilding.</summary>
    IServerServices Server { get; }
}
