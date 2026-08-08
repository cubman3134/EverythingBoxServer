using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core;
using EverythingBox.Server.Core.Debrid.RealDebrid;
using EverythingBox.Server.Core.Debrid.TorBox;
using EverythingBox.Server.Core.Download.QBittorrent;
using EverythingBox.Server.Core.Download.Transmission;
using EverythingBox.Server.Core.Http;
using EverythingBox.Server.Core.Providers.Torznab;

namespace EverythingBox.Server;

/// <summary>
/// Builds the search-and-resolve pipeline from <see cref="ServerConfig"/> plus whatever
/// indexers plugins registered. A user's config-built Torznab endpoint and a plugin's
/// provider are the same tier — both are concatenated into one provider list feeding a
/// single <see cref="TorrentGrabber"/>, never kept as separate pipelines.
/// <para>
/// Every config entry here degrades rather than throws: a blank/unparseable indexer
/// <c>BaseUrl</c>, a debrid block with no API key, or an unrecognized debrid/download-client
/// name is a likely user typo, and is logged and skipped. A server that refuses to start
/// because one config line is wrong is worse than one that starts and reports what it
/// ignored.
/// </para>
/// </summary>
public static class GrabberFactory
{
    /// <summary>
    /// Build a debrid service from config. This method is exposed separately so the
    /// debrid instance can be built once and shared across the plugin system and the
    /// grabber, ensuring there is exactly one instance for the process.
    /// </summary>
    /// <param name="transport">
    /// The message handler backing <paramref name="httpClient"/>'s actual network transport, so
    /// <see cref="WrapWithRetry"/>'s <see cref="RetryHandler"/> chains onto the SAME transport the
    /// caller's <see cref="HttpClient"/> uses instead of silently constructing an unrelated
    /// <see cref="HttpClientHandler"/> of its own. Null (the default) preserves the original
    /// behavior for callers that have no handler to hand over — <see cref="HttpClient"/> does not
    /// expose its own handler, so there is no way to recover one from <paramref name="httpClient"/>
    /// alone. Passing this is what makes it possible to intercept every indexer/debrid/download
    /// call a test makes by swapping one handler, rather than the swap being silently discarded.
    /// </param>
    public static IDebridService? BuildDebrid(
        ServerConfig config,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger("EverythingBox.Server.GrabberFactory");
        var http = WrapWithRetry(httpClient, transport);
        return BuildDebrid(config.Debrid, http, log);
    }

    /// <summary>
    /// Build a debrid service for a caller-supplied provider + key rather than from
    /// <see cref="ServerConfig"/> — what <see cref="Plugins.ServerServices.CreateDebrid"/> (and
    /// through it, <see cref="IServerServices.CreateDebrid"/>) calls. Wraps
    /// <paramref name="httpClient"/> in the same <see cref="RetryHandler"/> the config-driven
    /// overload above does, so a plugin-requested debrid retries transient failures exactly
    /// like a server-configured one, then goes through the same provider→service mapping.
    /// </summary>
    /// <param name="transport">See the overload of <see cref="BuildDebrid(ServerConfig, HttpClient, ILoggerFactory, HttpMessageHandler?)"/>.</param>
    /// <param name="maxWait">
    /// How long the built service should poll an uncached release before giving up and
    /// reporting it as still caching — see <see cref="Core.Debrid.TorBox.TorBoxOptions.MaxWait"/>/
    /// <see cref="Core.Debrid.RealDebrid.RealDebridOptions.MaxWait"/>. Defaults to
    /// <see cref="TimeSpan.Zero"/> ("cached-only"), matching the engine's behaviour before
    /// <c>Debrid.WaitSeconds</c> existed.
    /// </param>
    public static IDebridService? CreateDebrid(
        string? provider,
        string? apiKey,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? transport = null,
        TimeSpan maxWait = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger("EverythingBox.Server.GrabberFactory");
        var http = WrapWithRetry(httpClient, transport);
        return CreateDebrid(provider, apiKey, http, log, maxWait);
    }

    /// <param name="transport">See the overload of <see cref="BuildDebrid(ServerConfig, HttpClient, ILoggerFactory, HttpMessageHandler?)"/>.</param>
    public static (TorrentGrabber Grabber, IDebridService? Debrid) Build(
        ServerConfig config,
        HttpClient httpClient,
        IEnumerable<ITorrentProvider> pluginIndexers,
        ILoggerFactory loggerFactory,
        HttpMessageHandler? transport = null,
        IProviderPerformanceTracker? providerTracker = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(pluginIndexers);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger("EverythingBox.Server.GrabberFactory");

        // Every provider, the debrid service, and the download client share one HttpClient
        // wrapped in RetryHandler, so transient failures (429/502/503/504, network errors)
        // get exponential-backoff retries for free, without any of them opting in individually.
        var http = WrapWithRetry(httpClient, transport);

        var configIndexers = config.Indexers
            .Select(entry => BuildIndexer(entry, http, log))
            .Where(provider => provider is not null)
            .Select(provider => provider!);

        var providers = configIndexers.Concat(pluginIndexers).ToList();

        var debrid = BuildDebrid(config.Debrid, http, log);
        var downloadClient = BuildDownloadClient(config.DownloadClient, http, log);

        var options = new GrabberOptions
        {
            Ranking = config.Ranking,
            QuickGrabScore = config.Grabber.QuickGrabScore,
            ProviderTimeout = config.Grabber.ProviderTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(config.Grabber.ProviderTimeoutSeconds)
                : TimeSpan.Zero,
            PreferCachedReleases = config.Grabber.PreferCachedReleases,
        };

        var builder = new GrabberBuilder().Configure(options);
        foreach (var provider in providers)
            builder.AddProvider(provider);
        if (debrid is not null)
            builder.UseDebridService(debrid);
        if (downloadClient is not null)
            builder.UseDownloadClient(downloadClient);
        if (providerTracker is not null)
            builder.UseProviderTracker(providerTracker);

        return (builder.Build(), debrid);
    }

    /// <summary>
    /// Build a grabber with a pre-built debrid service, ensuring the same instance
    /// is used everywhere it's needed.
    /// </summary>
    /// <param name="transport">See the overload of <see cref="BuildDebrid(ServerConfig, HttpClient, ILoggerFactory, HttpMessageHandler?)"/>.</param>
    public static TorrentGrabber Build(
        ServerConfig config,
        HttpClient httpClient,
        IEnumerable<ITorrentProvider> pluginIndexers,
        ILoggerFactory loggerFactory,
        IDebridService? debrid,
        HttpMessageHandler? transport = null,
        IProviderPerformanceTracker? providerTracker = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(pluginIndexers);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger("EverythingBox.Server.GrabberFactory");

        var http = WrapWithRetry(httpClient, transport);

        var configIndexers = config.Indexers
            .Select(entry => BuildIndexer(entry, http, log))
            .Where(provider => provider is not null)
            .Select(provider => provider!);

        var providers = configIndexers.Concat(pluginIndexers).ToList();

        var downloadClient = BuildDownloadClient(config.DownloadClient, http, log);

        var options = new GrabberOptions
        {
            Ranking = config.Ranking,
            QuickGrabScore = config.Grabber.QuickGrabScore,
            ProviderTimeout = config.Grabber.ProviderTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(config.Grabber.ProviderTimeoutSeconds)
                : TimeSpan.Zero,
            PreferCachedReleases = config.Grabber.PreferCachedReleases,
        };

        var builder = new GrabberBuilder().Configure(options);
        foreach (var provider in providers)
            builder.AddProvider(provider);
        if (debrid is not null)
            builder.UseDebridService(debrid);
        if (downloadClient is not null)
            builder.UseDownloadClient(downloadClient);
        if (providerTracker is not null)
            builder.UseProviderTracker(providerTracker);

        return builder.Build();
    }

    /// <summary>
    /// Previously always constructed a brand-new <see cref="HttpClientHandler"/> here,
    /// silently discarding whatever transport <paramref name="shared"/> was actually built on —
    /// only its headers and timeout survived the wrap. That made the host's "one shared
    /// HttpClient" claim in <c>Program.cs</c> false for every indexer/debrid/download-client call
    /// (a proxy or custom handler configured on the shared client never reached them), and made
    /// the whole pipeline impossible to intercept in a test without this parameter — exactly the
    /// gap <c>SearchToStreamTests</c> exists to catch. <paramref name="transport"/>, when supplied,
    /// becomes <see cref="RetryHandler"/>'s inner handler instead.
    /// </summary>
    private static HttpClient WrapWithRetry(HttpClient shared, HttpMessageHandler? transport = null)
    {
        // disposeHandler: false because the handler is a shared singleton owned by the host (Program.cs).
        // Disposing any wrapped client would dispose the transport every other client depends on.
        var wrapped = new HttpClient(new RetryHandler(innerHandler: transport), disposeHandler: false) { Timeout = shared.Timeout };
        foreach (var header in shared.DefaultRequestHeaders)
            wrapped.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        return wrapped;
    }

    private static ITorrentProvider? BuildIndexer(IndexerConfig indexer, HttpClient http, ILogger log)
    {
        if (!Uri.TryCreate(indexer.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            log.LogWarning(
                "Indexer '{Name}' has a blank or unparseable BaseUrl ('{BaseUrl}'); skipping it.",
                string.IsNullOrWhiteSpace(indexer.Name) ? "(unnamed)" : indexer.Name, indexer.BaseUrl);
            return null;
        }

        return new TorznabProvider(http, new TorznabOptions
        {
            BaseUrl = baseUrl,
            ApiKey = string.IsNullOrWhiteSpace(indexer.ApiKey) ? null : indexer.ApiKey,
            Name = string.IsNullOrWhiteSpace(indexer.Name) ? "Torznab" : indexer.Name,
        });
    }

    private static IDebridService? BuildDebrid(DebridConfig? debrid, HttpClient http, ILogger log) =>
        debrid is null
            ? null
            : CreateDebrid(debrid.Provider, debrid.ApiKey, http, log, TimeSpan.FromSeconds(Math.Max(0, debrid.WaitSeconds)));

    /// <summary>
    /// The single provider→service mapping ("torbox"/"realdebrid") behind every debrid this
    /// host ever builds. <see cref="BuildDebrid(DebridConfig?, HttpClient, ILogger)"/> (the
    /// server's own config-driven debrid) and <see cref="Plugins.ServerServices.CreateDebrid"/>
    /// (a plugin-supplied provider + key, via <see cref="IServerServices.CreateDebrid"/>) both
    /// go through here rather than each keeping its own copy of the switch — so a plugin's
    /// debrid is built exactly the way a server-configured one would be. Null for a blank
    /// provider, a blank key, or a provider this host doesn't recognize.
    /// </summary>
    /// <param name="maxWait">Threaded straight into <c>TorBoxOptions.MaxWait</c>/
    /// <c>RealDebridOptions.MaxWait</c> — how long the built service polls an uncached
    /// release before reporting it as still caching.</param>
    internal static IDebridService? CreateDebrid(string? provider, string? apiKey, HttpClient http, ILogger log, TimeSpan maxWait)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            log.LogWarning("Debrid provider '{Provider}' has no API key; skipping it.", provider);
            return null;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "torbox" => new TorBoxService(http, new TorBoxOptions { ApiKey = apiKey, MaxWait = maxWait }),
            "realdebrid" => new RealDebridService(http, new RealDebridOptions { ApiToken = apiKey, MaxWait = maxWait }),
            _ => LogUnknown<IDebridService>(log, "debrid provider", provider),
        };
    }

    private static IDownloadClient? BuildDownloadClient(DownloadClientConfig? client, HttpClient http, ILogger log)
    {
        if (client is null || string.IsNullOrWhiteSpace(client.Kind))
            return null;

        if (!Uri.TryCreate(client.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            log.LogWarning(
                "Download client '{Kind}' has a blank or unparseable BaseUrl ('{BaseUrl}'); skipping it.",
                client.Kind, client.BaseUrl);
            return null;
        }

        return client.Kind.Trim().ToLowerInvariant() switch
        {
            "qbittorrent" => new QBittorrentClient(http, new QBittorrentOptions
            {
                BaseUrl = baseUrl,
                Username = string.IsNullOrWhiteSpace(client.Username) ? null : client.Username,
                Password = client.Password,
                DefaultCategory = string.IsNullOrWhiteSpace(client.Category) ? null : client.Category,
            }),
            "transmission" => new TransmissionClient(http, new TransmissionOptions
            {
                BaseUrl = baseUrl,
                Username = string.IsNullOrWhiteSpace(client.Username) ? null : client.Username,
                Password = client.Password,
            }),
            _ => LogUnknown<IDownloadClient>(log, "download client kind", client.Kind),
        };
    }

    private static T? LogUnknown<T>(ILogger log, string what, string value) where T : class
    {
        log.LogWarning("Unknown {What} '{Value}'; skipping it.", what, value);
        return null;
    }
}
