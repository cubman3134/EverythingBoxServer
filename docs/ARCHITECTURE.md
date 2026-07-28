# Architecture

EverythingBoxServer is an engine plus a plugin host. The engine knows how to search,
rank, resolve and serve media. It does not know where any media comes from — that
arrives as plugins.

## Projects

```
EverythingBox.Server.Abstractions/   the only assembly plugins reference
  Models/          MediaRequest+subtypes, TorrentResult, ReleaseInfo, DebridResult, …
  ITorrentProvider, IMediaSource, IMetadataSource, IPlugin, IPluginContext,
  IPluginRegistry, IServerServices
  IDebridService, IDownloadClient, IReleaseParser, ITorrentRanker,
  ITorrentDownloader, INestedArchiveReader, IFeedCookieSource, IResolverCache

EverythingBox.Server.Core/           the pipeline and infrastructure integrations
  TorrentGrabber, GrabberBuilder, GrabberOptions
  Parsing/DefaultReleaseParser        Ranking/DefaultTorrentRanker, RankingOptions
  Selection/MediaFileMatcher          Torrents/TorrentInfo (bencode)
  Debrid/{RealDebrid, TorBox, MagnetResolver}
  Download/{QBittorrent, Transmission}
  Providers/{DirectProviderBase, ExampleDirectProvider, Torznab/}
  Scraping/{RemoteZip, ArchiveReader, ArchiveNormalizer, ResolverCache, CurlTransport}
  Consoles/ConsoleCatalog             console names, aliases and ROM extensions
  Http/RetryHandler

EverythingBox.Server/                the host
  Program.cs, PluginHost, ManifestBuilder, SourceRouter, TorrentStreamResolver,
  DebridPicker, DirectDownloader, FileCache, SafeUrlGuard, ServerConfig
  Sources/{IndexerSearchSource, MetadataBackedVideoSource}

EverythingBox.Server.SampleSource/   a complete example plugin: a local folder
EverythingBox.Server.Console/        REPL harness for driving the pipeline
EverythingBox.Server.Tests/
```

Plugins reference **Abstractions only**. Core and the host are free to change without
breaking them.

## Plugin contract

### Entry point

```csharp
public interface IPlugin
{
    string Key { get; }              // namespaces this plugin's ids and config section
    string DisplayName { get; }
    Version ApiVersion { get; }      // checked against the host's; mismatch = refuse to load
    void Configure(IPluginRegistry registry, IPluginContext context);
}

public interface IPluginRegistry
{
    void AddIndexer(ITorrentProvider provider);
    void AddSource(IMediaSource source);
    void AddMetadata(IMetadataSource metadata);
}

public interface IPluginContext
{
    ILoggerFactory Loggers { get; }
    HttpClient Http { get; }              // shared, already wrapped in RetryHandler
    T? GetConfig<T>() where T : class;    // this plugin's own config section
    string CacheDirectory { get; }        // plugin-private, server-managed
    IServerServices Server { get; }       // grabber, debrid, downloader, file cache
}
```

### Tier 1 — indexers

An indexer implements `ITorrentProvider` and nothing else. The host feeds it into
`TorrentGrabber`, so it inherits dedupe, release parsing, ranking, cached-first ordering,
debrid resolution, single-file extraction and the BitTorrent fallback automatically.

`DirectProviderBase` reduces a typical HTTP indexer to three methods: build the query,
build the URL, parse the response. `ExampleDirectProvider` is a copy-me stub.

### Tier 2 — sources

A source that doesn't fit the torrent pipeline — a direct-download site, a
chapter-structured library, a local folder — implements `IMediaSource` and owns its own
flow end to end.

```csharp
public interface IMediaSource
{
    string Key { get; }
    IReadOnlyList<CatalogDescriptor> Catalogs { get; }   // id, display name, media type

    Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct);
    Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct);
    Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct);

    // Optional. Implement only when the client cannot fetch the URL itself —
    // an authenticated host, or one that rejects the client's TLS fingerprint.
    Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => Task.FromResult<ProxyResponse?>(null);

    // Optional. Cache expensive state at startup; config decides whether failure is fatal.
    Task<WarmUpResult> WarmUpAsync(CancellationToken ct)
        => Task.FromResult(WarmUpResult.NotApplicable);
}
```

`DetailAsync` powers expandable items — a series into episodes, a volume into chapters.

`SourceContext` carries what the request knows: the caller's own debrid credentials when
they supplied them, whether the client can fetch a URL itself via curl, and the
cancellation budget.

Implementing `OpenAsync` gets the source a host-owned `/proxy/{source}/{enc}/{name}`
route that forwards range headers and delegates the fetch back to the plugin. The host
does not need to know why a given source requires proxying.

### Metadata

`IMetadataSource` supplies browse catalogs and episode listings for movies and series.
The host's `MetadataBackedVideoSource` pairs any registered metadata source with any
registered indexers, so browsing and resolving stay decoupled.

## Sources the host ships

Three, none of which name a third-party site:

- **`IndexerSearchSource`** — title search across every registered indexer, listed as
  releases. With a Torznab endpoint configured and no plugins at all, this alone gives
  you working search catalogs.
- **`MetadataBackedVideoSource`** — movie and series browsing through any registered
  `IMetadataSource`, resolved through the pipeline.
- **`LocalFolderSource`** (sample project) — scans a directory and serves files.

## Routing

Every id the server emits is prefixed with its owner: `{sourceKey}:{payload}`, or `idx:`
for anything that came through the torrent pipeline. `SourceRouter` splits on the first
colon and dispatches. Payloads are opaque to the host, so each plugin chooses its own
encoding.

`ManifestBuilder` unions every registered catalog and media type into the addon manifest
the EverythingBox client consumes. Installing a plugin changes the manifest; the client
needs no changes.

## Loading and isolation

Plugins live at `plugins/<key>/<key>.dll`. Each loads into its own `AssemblyLoadContext`
so their dependencies cannot collide with each other or with the host's.

The Abstractions assembly is deliberately *not* loaded per-plugin — each context resolves
it back to the default context, so `ITorrentProvider` in a plugin is the same type as
`ITorrentProvider` in the host. Copying Abstractions into a plugin folder would break
this; the build template excludes it.

Failures are contained:

| Failure | Behavior |
|---|---|
| API-version mismatch | Log, skip plugin, server starts |
| Throws in `Configure` | Log, skip plugin, server starts |
| Throws during a search | Log, drop from that response; other sources still answer |
| Resolve returns nothing | Empty streams response |
| Required `WarmUpAsync` fails | Retry until deadline, then refuse to serve |
| Resolved URL is not client-safe | Refuse, log, return empty streams |

## Stream request flow

```
GET /<token>/stream/{type}/{id}.json?n=&dl=
   │
   ├─ SourceRouter splits "key:payload"
   │
   ├─ key = plugin source ──► IMediaSource.ResolveAsync(payload, n, ctx)
   │                            └─ may call ctx.Server.Grabber / Debrid / Downloader
   │
   └─ key = "idx"         ──► TorrentStreamResolver
                                ├─ cached on debrid ──► direct link, single file extracted
                                ├─ uncached, within size cap ──► self-download ──► /files/…
                                └─ otherwise ──► notice ("caching, retry shortly")
   │
   ▼
SafeUrlGuard: a relative addon path, or http(s). Anything else is refused and logged.
   │
   ▼
{ url, mime }  |  { url, mime, curl:true }  |  { streams:[], notice }
```

`SafeUrlGuard` lives in the host on purpose. The guarantee that the client is never
handed a magnet or an unknown scheme has to hold for plugin code the host did not write.

## Server-owned services

Plugins reach these through `IPluginContext.Server`:

| Service | Does |
|---|---|
| `Grabber` | Run a `MediaRequest` through the full search-and-rank pipeline |
| `Debrid` | Resolve a torrent to direct links on the configured (or caller's) account |
| `Downloader` | Self-download over BitTorrent, extracting only the wanted files |
| `FileCache` | Build a file once under concurrent requests, then serve it from `/files` |

`FileCache` deduplicates in-flight builds by name, so ten simultaneous requests for the
same generated file produce one build.

## `?n=` and rejection

The stream route takes an optional `?n=K` for the K-th best source. A user who rejects a
result gets the next candidate without a re-search. Pipeline results are ordered by the
ranker; a plugin source decides its own ordering.
