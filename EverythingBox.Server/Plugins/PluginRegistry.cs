using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Plugins;

/// <summary>Collects what one plugin registers. A fresh instance per plugin, so a
/// plugin that throws half way through registration leaves nothing behind.</summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, IMediaSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ITorrentProvider> _indexers = [];
    private readonly List<IMetadataSource> _metadata = [];
    private IProviderPerformanceTracker? _providerTracker;

    public IReadOnlyCollection<IMediaSource> Sources => _sources.Values;

    /// <summary>The tracker this plugin registered, or null if none. Consumed by the
    /// host to feed GrabberBuilder.UseProviderTracker.</summary>
    public IProviderPerformanceTracker? ProviderTracker => _providerTracker;

    /// <summary>Indexers registered so far. Consumed by the host to feed the pipeline
    /// that backs <see cref="IServerServices.Grabber"/>.</summary>
    public IReadOnlyCollection<ITorrentProvider> Indexers => _indexers;

    /// <summary>Metadata sources registered so far. Consumed by the host to pair
    /// browsing with the pipeline.</summary>
    public IReadOnlyCollection<IMetadataSource> MetadataSources => _metadata;

    public void AddSource(IMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateKey(source.Key, nameof(source));

        if (!_sources.TryAdd(source.Key, source))
            throw new InvalidOperationException($"A source with key '{source.Key}' is already registered.");
    }

    // Deliberately does NOT read provider.Name. Name is plugin-authored code and can
    // throw (the host learned this the hard way in milestone 1, where reading a plugin
    // property outside a try/catch let one bad plugin 500 the manifest for everyone).
    // Do not "helpfully" add a name check here — it reintroduces that hazard.
    public void AddIndexer(ITorrentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _indexers.Add(provider);
    }

    // Deliberately does NOT read metadata.Name or metadata.SupportedMediaTypes. These
    // are plugin-authored code and can throw (the host learned this the hard way in
    // milestone 1, where reading a plugin property outside a try/catch let one bad
    // plugin 500 the manifest for everyone). Do not "helpfully" add a member check here
    // — it reintroduces that hazard.
    public void AddMetadata(IMetadataSource metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata.Add(metadata);
    }

    // Deliberately does NOT invoke Prioritize/Record on the tracker. Those are
    // plugin-authored code and can throw (the host learned this the hard way in
    // milestone 1, where reading a plugin property outside a try/catch let one bad
    // plugin 500 the manifest for everyone). Do not "helpfully" probe the tracker here
    // — it reintroduces that hazard.
    //
    // At most one tracker applies across the whole server (it orders ONE provider
    // list), so a second registration is a real configuration mistake, not something
    // to silently paper over. It throws — same as AddSource's duplicate-key check —
    // rather than silently replacing the first tracker, which would leave whoever
    // wrote the first registration wondering why it's never consulted.
    public void AddProviderTracker(IProviderPerformanceTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        if (_providerTracker is not null)
            throw new InvalidOperationException(
                "A provider tracker is already registered — at most one applies across the whole server.");

        _providerTracker = tracker;
    }

    internal static void ValidateKey(string key, string paramName)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be non-empty.", paramName);

        if (key.Contains(':'))
            throw new ArgumentException($"Key '{key}' must not contain ':' — it separates the key from the payload in every id.", paramName);
    }
}
