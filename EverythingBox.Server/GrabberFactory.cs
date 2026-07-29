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
    public static (TorrentGrabber Grabber, IDebridService? Debrid) Build(
        ServerConfig config,
        HttpClient httpClient,
        IEnumerable<ITorrentProvider> pluginIndexers,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(pluginIndexers);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var log = loggerFactory.CreateLogger("EverythingBox.Server.GrabberFactory");

        // Every provider, the debrid service, and the download client share one HttpClient
        // wrapped in RetryHandler, so transient failures (429/502/503/504, network errors)
        // get exponential-backoff retries for free, without any of them opting in individually.
        var http = WrapWithRetry(httpClient);

        var configIndexers = config.Indexers
            .Select(entry => BuildIndexer(entry, http, log))
            .Where(provider => provider is not null)
            .Select(provider => provider!);

        var providers = configIndexers.Concat(pluginIndexers).ToList();

        var debrid = BuildDebrid(config.Debrid, http, log);
        var downloadClient = BuildDownloadClient(config.DownloadClient, http, log);

        var options = new GrabberOptions { Ranking = config.Ranking };

        var builder = new GrabberBuilder().Configure(options);
        foreach (var provider in providers)
            builder.AddProvider(provider);
        if (debrid is not null)
            builder.UseDebridService(debrid);
        if (downloadClient is not null)
            builder.UseDownloadClient(downloadClient);

        return (builder.Build(), debrid);
    }

    private static HttpClient WrapWithRetry(HttpClient shared)
    {
        var wrapped = new HttpClient(new RetryHandler()) { Timeout = shared.Timeout };
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

    private static IDebridService? BuildDebrid(DebridConfig? debrid, HttpClient http, ILogger log)
    {
        if (debrid is null || string.IsNullOrWhiteSpace(debrid.Provider))
            return null;

        if (string.IsNullOrWhiteSpace(debrid.ApiKey))
        {
            log.LogWarning("Debrid provider '{Provider}' is configured with no API key; skipping it.", debrid.Provider);
            return null;
        }

        return debrid.Provider.Trim().ToLowerInvariant() switch
        {
            "torbox" => new TorBoxService(http, new TorBoxOptions { ApiKey = debrid.ApiKey }),
            "realdebrid" => new RealDebridService(http, new RealDebridOptions { ApiToken = debrid.ApiKey }),
            _ => LogUnknown<IDebridService>(log, "debrid provider", debrid.Provider),
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
