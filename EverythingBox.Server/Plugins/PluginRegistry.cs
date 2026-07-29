using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Plugins;

/// <summary>Collects what one plugin registers. A fresh instance per plugin, so a
/// plugin that throws half way through registration leaves nothing behind.</summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, IMediaSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ITorrentProvider> _indexers = [];

    public IReadOnlyCollection<IMediaSource> Sources => _sources.Values;

    /// <summary>Indexers registered so far. Consumed by the host to feed the pipeline
    /// that backs <see cref="IServerServices.Grabber"/>.</summary>
    public IReadOnlyCollection<ITorrentProvider> Indexers => _indexers;

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

    internal static void ValidateKey(string key, string paramName)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be non-empty.", paramName);

        if (key.Contains(':'))
            throw new ArgumentException($"Key '{key}' must not contain ':' — it separates the key from the payload in every id.", paramName);
    }
}
