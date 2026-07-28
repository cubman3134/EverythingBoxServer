using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core;

/// <summary>
/// Fluent helper for assembling a <see cref="TorrentGrabber"/>.
/// <code>
/// var grabber = new GrabberBuilder()
///     .AddProvider(new ExampleDirectProvider(httpClient, baseUrl))
///     .Configure(new GrabberOptions { /* ... */ })
///     .Build();
/// </code>
/// </summary>
public sealed class GrabberBuilder
{
    private readonly List<ITorrentProvider> _providers = [];
    private ITorrentRanker? _ranker;
    private IReleaseParser? _parser;
    private GrabberOptions? _options;
    private IDownloadClient? _downloadClient;
    private IDebridService? _debridService;
    private IProviderPerformanceTracker? _providerTracker;

    public GrabberBuilder AddProvider(ITorrentProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    public GrabberBuilder UseRanker(ITorrentRanker ranker)
    {
        _ranker = ranker;
        return this;
    }

    public GrabberBuilder UseParser(IReleaseParser parser)
    {
        _parser = parser;
        return this;
    }

    public GrabberBuilder Configure(GrabberOptions options)
    {
        _options = options;
        return this;
    }

    /// <summary>Set the download client used by <c>GrabAndDownloadAsync</c>.</summary>
    public GrabberBuilder UseDownloadClient(IDownloadClient downloadClient)
    {
        _downloadClient = downloadClient;
        return this;
    }

    /// <summary>Set the debrid service used by <c>GrabAndResolveAsync</c>.</summary>
    public GrabberBuilder UseDebridService(IDebridService debridService)
    {
        _debridService = debridService;
        return this;
    }

    /// <summary>Set the tracker that learns and prioritizes the best providers.</summary>
    public GrabberBuilder UseProviderTracker(IProviderPerformanceTracker tracker)
    {
        _providerTracker = tracker;
        return this;
    }

    public TorrentGrabber Build() => new(_providers, _ranker, _parser, _options, _downloadClient, _debridService, _providerTracker);
}
