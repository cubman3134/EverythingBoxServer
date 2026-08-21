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
    private IMusicLibrary? _musicLibrary;
    private readonly List<IRomhackSource> _romhackSources = new();

    public IReadOnlyCollection<IMediaSource> Sources => _sources.Values;

    /// <summary>The tracker this plugin registered, or null if none. Consumed by the
    /// host to feed GrabberBuilder.UseProviderTracker.</summary>
    public IProviderPerformanceTracker? ProviderTracker => _providerTracker;

    /// <summary>The music library this plugin registered, or null if none. Consumed by the
    /// host to back the Subsonic-style API.</summary>
    public IMusicLibrary? MusicLibrary => _musicLibrary;
    public IReadOnlyList<IRomhackSource> RomhackSources => _romhackSources;

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

    // Deliberately does NOT invoke any member of the library (Artists/Search/OpenTrackAsync …).
    // Those are plugin-authored code and can throw (the host learned this the hard way in
    // milestone 1, where reading a plugin property outside a try/catch let one bad plugin 500
    // the manifest for everyone). Do not "helpfully" probe the instance here — it reintroduces
    // that hazard.
    //
    // At most one music library applies across the whole server (one Subsonic surface serves
    // from it), so a second registration is a real configuration mistake, not something to
    // silently paper over. It throws — same as AddSource's duplicate-key check — rather than
    // silently replacing the first library, which would leave whoever wrote the first
    // registration wondering why it's never consulted.
    public void AddMusicLibrary(IMusicLibrary music)
    {
        ArgumentNullException.ThrowIfNull(music);

        if (_musicLibrary is not null)
            throw new InvalidOperationException(
                "A music library is already registered — at most one applies across the whole server.");

        _musicLibrary = music;
    }

    // Many romhack sources DO apply at once — a game's hacks are fanned out across all of them — so
    // this appends rather than refusing a second registration. Registering the same instance twice is
    // still a mistake, and would double every row that source returns.
    public void AddRomhackSource(IRomhackSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_romhackSources.Contains(source))
            throw new InvalidOperationException(
                "That romhack source is already registered — registering it twice would double its rows.");

        _romhackSources.Add(source);
    }

    internal static void ValidateKey(string key, string paramName)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must be non-empty.", paramName);

        if (key.Contains(':'))
            throw new ArgumentException($"Key '{key}' must not contain ':' — it separates the key from the payload in every id.", paramName);
    }
}
