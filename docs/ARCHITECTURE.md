# Architecture

EverythingBoxServer is a plugin host. It knows how to load a plugin, route a request to
the source that owns it, and serve the addon protocol EverythingBox speaks. It does not
know where any media comes from — that arrives entirely as plugins.

## Projects

```
EverythingBox.Server.Abstractions/   the only assembly plugins reference
  IPlugin, IPluginRegistry, IPluginContext         (Abstractions/IPlugin.cs)
  IMediaSource                                     (Abstractions/IMediaSource.cs)
  IServerServices                                  (Abstractions/IServerServices.cs)
  MediaTypeNames                                   (Abstractions/MediaTypeNames.cs)
  CatalogDescriptor, MediaTypeDescriptor, CatalogItem, SourceCatalog,
  SourceStream, SourceContext, ProxyResponse, WarmUpResult             (Abstractions/Catalog.cs)
  ServerApi                                        (Abstractions/ServerApi.cs)
  ITorrentGrabber, ITorrentProvider, IDebridService, IDownloadClient,
  RankingOptions                                   (Abstractions/Pipeline/)
  MediaRequest and its subclasses (MovieRequest, TvRequest, ...)       (Abstractions/Requests/)
  TorrentResult, DebridResult, GrabResult                              (Abstractions/Results/)
  IMetadataSource, MetadataItem, MetadataEpisode   (Abstractions/Metadata/IMetadataSource.cs)

EverythingBox.Server/                the host
  Program.cs                          composition root and route mounting
  GrabberFactory.cs                   builds the torrent pipeline from ServerConfig
  Plugins/PluginHost, PluginRegistry, PluginContext, PluginLoadContext, ServerServices
  Sources/IndexerSearchSource.cs      the idx: search catalogs (see "Search, out of the box")
  Sources/MetadataBackedVideoSource.cs the meta: browse catalogs (see "Browse: the meta: catalogs")
  Sources/ReleaseStreamResolver.cs    turns a chosen release into a stream via IDebridService;
                                      ResolveAllAsync additionally resolves every playable
                                      option in one debrid round trip (used by MetadataBackedVideoSource)
  Routing/SourceRouter
  AddonEndpoints                      manifest/catalog/detail/meta/stream/proxy/files routes
  ManifestBuilder, SafeUrlGuard, FileCache, ServerConfig

EverythingBox.Server.SampleSource/   a complete example plugin: a local folder
EverythingBox.Server.Tests/          xUnit tests for the host and Abstractions
tests/TestPlugin.Good, TestPlugin.Bad, TestPlugin.Dup
                                      fixture plugins PluginHostTests loads to exercise
                                      the success/failure/duplicate-key paths

EverythingBox.Server.Core/           standalone torrent search/parse/rank/resolve
                                      pipeline library — BCL-only, no PackageReference,
                                      enforced by CoreDependencyTests. The host
                                      (GrabberFactory.cs) wires it up from config; a
                                      plugin author can also build on it directly. See
                                      "The torrent pipeline" below.
EverythingBox.Server.Core.Tests/     xUnit tests for Core
```

**Namespace convention.** `EverythingBox.Server.Abstractions` is deliberately *flat*: every
type lives in the single `EverythingBox.Server.Abstractions` namespace regardless of which
folder (`Pipeline/`, `Requests/`, `Results/`) it's filed under, so a plugin author needs
exactly one `using`. `EverythingBox.Server.Core` does the opposite on purpose: its
namespaces mirror its folders (`EverythingBox.Server.Core.Download.QBittorrent`,
`EverythingBox.Server.Core.Scraping`, etc.), because it's a larger surface a consumer
browses by area rather than referencing wholesale. Both are internally consistent today;
this is the reasoning, recorded once rather than left implicit.

Plugins reference **Abstractions only**. The host is free to change without breaking
them, as long as the `IPlugin`/`IMediaSource` contract and `ServerApi.VersionString`
stay compatible.

## Plugin contract

### Entry point

```csharp
public interface IPlugin
{
    string Key { get; }              // namespaces this plugin's ids and config section; must not contain ':'
    string DisplayName { get; }
    Version ApiVersion { get; }      // set to new Version(ServerApi.VersionString); checked against the host's, mismatch = refuse to load
    void Configure(IPluginRegistry registry, IPluginContext context);
}

public interface IPluginRegistry
{
    void AddSource(IMediaSource source);

    // Register an indexer instead of a whole source: it inherits the shared pipeline's
    // dedupe, release parsing, ranking, cached-first ordering, and single-file extraction
    // for free. Merged with every config-defined Indexers entry into the same grabber.
    void AddIndexer(ITorrentProvider provider);

    // Register a metadata source: browsing decoupled from any one indexer. The host
    // pairs it with the same shared pipeline via the built-in MetadataBackedVideoSource
    // ("meta:" catalogs) — see "Browse: the meta: catalogs" below.
    void AddMetadata(IMetadataSource metadata);
}

public interface IPluginContext
{
    ILoggerFactory Loggers { get; }
    HttpClient Http { get; }              // shared; the host owns its lifetime, plugins must not dispose it
    string CacheDirectory { get; }        // plugin-private, created before Configure runs
    T? GetConfig<T>() where T : class;    // this plugin's own config section, or null if absent
    IServerServices Server { get; }       // host capabilities a plugin can borrow — see below
}
```

There are three registration tiers today. `IMediaSource` (`AddSource`) owns its own
catalogs, its own search, and its own stream resolution end to end — the tier every
shipped plugin (`EverythingBox.Server.SampleSource`) uses. `ITorrentProvider`
(`AddIndexer`) is smaller: it plugs into the shared `EverythingBox.Server.Core` pipeline
instead of implementing search itself, the same pipeline a config-defined `Indexers`
entry feeds — see "The torrent pipeline" below. `IMetadataSource` (`AddMetadata`) is
smaller still: it supplies only what to browse (titles, and episodes for a series);
the host's built-in `MetadataBackedVideoSource` pairs it with that same shared
pipeline for locating and resolving a release — see "Browse: the meta: catalogs" below.

### `IServerServices`

Reached through `IPluginContext.Server` (`Plugins/ServerServices.cs` implements it):

```csharp
public interface IServerServices
{
    ITorrentGrabber Grabber { get; }   // fed by every indexer, config- and plugin-registered alike
    IDebridService? Debrid { get; }    // null when the server has none configured
    IFileCache Files { get; }
}
```

`Grabber` must not be called from `IPlugin.Configure` — the host cannot build the real
grabber until every plugin has finished registering its indexers, so a call made during
registration throws `InvalidOperationException` rather than silently returning one with
zero indexers. Hold the reference during `Configure` and call it later, while serving a
request.

### `IMediaSource`

```csharp
public interface IMediaSource
{
    string Key { get; }                                   // namespaces every id this source emits; must not contain ':'
    IReadOnlyList<CatalogDescriptor> Catalogs { get; }
    IReadOnlyList<MediaTypeDescriptor> MediaTypes => [];   // optional; presentation for a media type the client doesn't know natively

    Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct);

    // Expand one item — a series into episodes, a volume into chapters.
    Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct);

    // index selects the N-th best source, so a user who rejects a result gets the next one.
    Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct);

    // Optional. Implement only when the client cannot fetch the URL itself.
    Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => Task.FromResult<ProxyResponse?>(null);

    // Optional. The host calls this once per registered source at startup, before it takes
    // traffic — a single best-effort attempt, not a retry loop or a startup gate. A throw
    // or a Failed result is a logged warning; the server starts regardless.
    Task<WarmUpResult> WarmUpAsync(CancellationToken ct)
        => Task.FromResult(WarmUpResult.NotApplicable);
}
```

`SourceContext` currently carries one thing: `ClientCanCurl`, whether the requesting
client can fetch a URL itself rather than needing it proxied.

Implementing `OpenAsync` gets the source a host-owned
`/proxy/{sourceKey}/{id}/{name}` route that forwards the `Range` header and relays
bytes back; the host does not need to know why a given source requires proxying.

## Sources the host ships

Two, both constructed directly in `Program.cs` and appended after every plugin's own
sources (so a plugin can never be shadowed by either): `IndexerSearchSource`
(`Sources/IndexerSearchSource.cs`, key `idx`, see "Search, out of the box" below) and
`MetadataBackedVideoSource` (`Sources/MetadataBackedVideoSource.cs`, key `meta`, see
"Browse: the meta: catalogs" below). Neither carries content of its own: `idx:` needs
at least one indexer (config or plugin-registered) before it declares any catalog, and
`meta:` needs at least one registered `IMetadataSource` before it declares `meta:movies`
or `meta:series`. With neither configured, neither source's catalogs appear in the
manifest at all — the same "installed but empty" shape a fresh `plugins/` folder has.

Beyond that, zero — no plugin ships with the host. `EverythingBox.Server.SampleSource`
is a separate, optional plugin project — `LocalFolderSource` — that ships as a worked
example: it scans the folders in its config, lists media files it recognizes by
extension, and serves them through `OpenAsync`/the proxy route. It is not loaded by the
host unless its build output is placed under `plugins/local/` like any other plugin.

## Search, out of the box

`IndexerSearchSource` (`Sources/IndexerSearchSource.cs`, key `idx`) exposes one
search-only catalog per media type the pipeline understands — movies, series, music,
audiobooks, books, comics — backed by an `ITorrentGrabber` built from every configured
`Indexers` entry plus whatever indexers plugins register via `AddIndexer`. It is an
ordinary `IMediaSource`: `SourceRouter` reaches it the same way it reaches every other
source, by splitting `"idx:{payload}"` on the colon — there is no special-cased routing.

A catalog is search-only: opening it with no query returns a "search to see results"
placeholder rather than firing a blank query at every indexer. A query is translated to
the matching `MediaRequest` subclass via `MediaTypeNames` (movie catalog → `MovieRequest`,
series → `TvRequest`, etc.), sent through `ITorrentGrabber.SearchRankedAsync` (see
"The torrent pipeline" below — `Ranking` applies here), and every
result becomes one `CatalogItem` whose id opaquely encodes the underlying
`TorrentResult` (title, provider, hash, magnet/download URL, size, seeders), so it
round-trips through the client without any server-side state.

`Sources/ReleaseStreamResolver.cs` resolves a chosen item: it calls the configured
`IDebridService.ResolveAsync` and maps the result — `Resolved` becomes a playable
`{ url, mime }`, `Pending` becomes a notice-only stream (`{ streams: [], notice }`,
"still caching" or similar), `Failed` or no configured debrid becomes an empty streams
response, the same shape `SourceStream` and the stream route already use for every other
source. **There is deliberately no self-download fallback** — an uncached release comes
back as a notice, not a download queued on the host's own hardware; see "Planned" below.

`GrabberFactory.cs` is what builds the `ITorrentGrabber` and `IDebridService` these two
sources use, from `ServerConfig`'s `Indexers`/`Debrid`/`DownloadClient`/`Ranking` keys —
see "The torrent pipeline" below for what each key does and how a malformed entry
degrades.

## Browse: the `meta:` catalogs

`MetadataBackedVideoSource` (`Sources/MetadataBackedVideoSource.cs`, key `meta`) turns
every registered `IMetadataSource` (`AddMetadata`) into browsable `meta:movies` and
`meta:series` catalogs — the "browse, then play" flow, as opposed to `idx:`'s
search-only shelves. Like `idx:`, a shelf is only declared if something can fill it:
`meta:movies` appears only when at least one registered `IMetadataSource.SupportedMediaTypes`
includes `"movie"` (independently for `meta:series`/`"series"`); with no metadata
plugin installed, neither catalog appears.

`SearchAsync` asks every metadata source that supports the catalog's media type for
`IMetadataSource.BrowseAsync`, and merges their `MetadataItem`s into one catalog; a
series item comes back `Expandable: true`, a movie does not. `DetailAsync` expands a
series id (one carrying no season/episode of its own) into its episodes via
`IMetadataSource.EpisodesAsync`, called with the source's own id for that title —
`MetadataItem.Id` — not the title string. An id that already carries a season and
episode (an episode id, itself still media type `"series"`) has nothing further to
expand and returns empty, the same as a movie id would.

`ResolveAsync` is where `meta:` meets the torrent pipeline: a movie id or an episode id
(a bare series id is not resolvable — it has no single release) becomes a `MovieRequest`
or `TvRequest` and is searched via `ITorrentGrabber.SearchRankedAsync` — best-first,
ranked candidates, same as `GrabAsync` uses, unlike `idx:`'s own catalogs which call
the same method (see "Search, out of the box" above). `index` walks playable *files*
before moving to the next candidate release: for each ranked release in turn,
`ReleaseStreamResolver.ResolveAllAsync` resolves every option that release's debrid
round trip yields in one call (Pending wraps as a single notice option, Failed yields
none, Resolved yields one option per narrowed link — see `ReleaseStreamResolverTests`
for both branches), and `index` is consumed against that count before falling through
to the next candidate. This lets a user reject one file (an unwanted quality, a season
pack's wrong episode) and land on the next real option without abandoning the whole
release.

Every `IMetadataSource` call above (`SupportedMediaTypes`, `BrowseAsync`,
`EpisodesAsync`) is guarded the same way plugin-authored code is guarded everywhere
else in this host: a throw degrades to "skip this source", never takes the whole
catalog or expansion down.

**Metadata only supplies what to browse.** Locating and resolving a release still goes
through the `Indexers`/`Debrid` config described above — a metadata plugin with no
indexer configured browses fine but resolves nothing, the same "installed but
nothing to serve" shape `idx:` has with no indexer.

## Routing

Every id the server emits is prefixed with its owner: `{sourceKey}:{payload}`.
`SourceRouter` (`Routing/SourceRouter.cs`) splits on the first `:` and dispatches to the
matching source; payloads are opaque to the host, so each plugin chooses its own
encoding.

`ManifestBuilder` unions every registered source's catalogs and media types into the
addon manifest the EverythingBox client consumes. Installing a plugin changes the
manifest; the client needs no changes.

## Addon routes

All routes below except `/` and `/health` are mounted under an optional token prefix
(`/<token>/...`) when `AccessToken` is configured.

| Route | Does |
|---|---|
| `GET /` | Plain-text usage hint |
| `GET /health` | `{ ok: true }` |
| `GET /manifest.json` | Built by `ManifestBuilder` from every loaded source |
| `GET /catalog/{catalogId}.json` | `SourceRouter.TryResolve` → `IMediaSource.SearchAsync` |
| `GET /catalog/{catalogId}/{extra}.json` | Same, with `search=...` parsed out of `{extra}` |
| `GET /detail/{type}/{id}.json` | `IMediaSource.DetailAsync` — for `meta:`, expands a series into its episodes |
| `GET /meta/{type}/{id}.json` | Always an empty object — unrelated to `IMetadataSource`/the `meta:` catalogs above; no source populates a rich detail panel here yet |
| `GET /stream/{type}/{id}.json?n=&dl=` | `IMediaSource.ResolveAsync`, then `SafeUrlGuard` |
| `GET /proxy/{sourceKey}/{id}/{name}` | `IMediaSource.OpenAsync`, relayed with range support |
| `GET /files/{name}` | Serves a file `FileCache` already built, with range support |

## Loading and isolation

Plugins live at `plugins/<key>/<dll>.dll` — any `.dll` in the folder is a candidate, not
just one named after the folder. `PluginHost.Load` (`Plugins/PluginHost.cs`) enumerates
plugin directories, and each candidate assembly loads into its own
`AssemblyLoadContext` (`PluginLoadContext`) so its dependencies cannot collide with
another plugin's or the host's.

The Abstractions assembly (and `Microsoft.Extensions.Logging.Abstractions`) is
deliberately *not* loaded per-plugin — `PluginLoadContext.Load` returns `null` for those
names so resolution falls through to the default context, which is what makes a
plugin's `IMediaSource` the same runtime type as the host's. A plugin project must
reference Abstractions with `Private="false"` (see
`EverythingBox.Server.SampleSource.csproj`); copying a second copy into a plugin's
output folder loads it twice and breaks every cast between the host's and the plugin's
`IMediaSource`.

Failures are contained — logged and skipped, never a crash of the whole process:

| Failure | Behavior |
|---|---|
| Assembly is not a loadable managed DLL | Skip that file, keep scanning the folder |
| Plugin type has no public parameterless constructor | Log, skip that type |
| Plugin key invalid (empty or contains `:`) | Log, skip the plugin |
| `ApiVersion` incompatible with `ServerApi.Current` | Log, skip the plugin, server starts |
| Plugin key already used by another loaded plugin | Log, skip the later one |
| `Configure` throws | Log, skip the plugin, server starts |
| Two sources (from different plugins) register the same key | Log, keep the first, drop the later one |
| `Catalogs`/`MediaTypes` throws while building the manifest | Log, omit that source from the manifest; every other source still appears |
| `SearchAsync`/`DetailAsync`/`ResolveAsync`/`OpenAsync` throws | Log, return the route's normal "nothing found" shape (empty catalog / empty streams / 404) |
| A source returns a null `SourceCatalog`, or one with null `Items` | Treated as "nothing found", not a crash |
| `WarmUpAsync` throws or returns `Failed` | Log a warning; does not block the server or another source's warm-up |
| No source claims a requested id | Empty catalog / empty streams response |
| `ResolveAsync` returns `null` | Empty streams response |
| Resolved URL is not client-safe | Refuse, log, return empty streams |

## `SafeUrlGuard`

`SafeUrlGuard.IsClientSafe` (`SafeUrlGuard.cs`) is the one thing a plugin cannot bypass:
a stream URL from `ResolveAsync` is only handed to the client if it's either a relative
path (served by this host) or an absolute `http`/`https` URL. Anything else — a magnet
link, a local file path, an unrecognized scheme — is refused and logged before it
reaches the client. This runs in the host on purpose, because the guarantee has to hold
for plugin code the host did not write.

## `FileCache`

`FileCache.GetOrBuildAsync` (`FileCache.cs`) builds a served file once even under
concurrent requests — the build function runs exactly once per name via a
`Lazy<Task<T>>` keyed by the served name — and evicts a failed build so a retry can
succeed. `GET /files/{name}` serves whatever was built, from `cache.Root`, with range
processing enabled.

## Stream request flow

```
GET /<token>/stream/{type}/{id}.json?n=&dl=
   │
   ├─ SourceRouter splits "key:payload"; no match ──► { streams: [] }
   │
   ▼
IMediaSource.ResolveAsync(payload, n ?? 0, ctx)
   │
   ├─ null ──► { streams: [] }
   ├─ empty Url, Notice set ──► { streams: [], notice }
   └─ Url set ──► SafeUrlGuard.IsClientSafe?
                     ├─ no  ──► log, { streams: [] }
                     └─ yes ──► { url, mime } or { url, mime, curl: true } when the source set Curl
```

`ctx.ClientCanCurl` is set from `?dl=curl` — a source can use it to decide whether to
hand the client a URL directly instead of routing through its own proxy.

## The torrent pipeline (`EverythingBox.Server.Core`)

A separate, standalone library — no `PackageReference` (`EverythingBox.Server.Core.csproj`,
enforced by `CoreDependencyTests.cs`) — for the search → parse → rank → resolve pipeline
torrent-backed media needs. The host wires it up via `GrabberFactory.cs`
(`EverythingBox.Server/GrabberFactory.cs`) and exposes it as the `idx:` search catalogs
described under "Search, out of the box" above; a plugin author can also build directly
on the library, the same as before this wiring existed.

`TorrentGrabber` (`TorrentGrabber.cs`) is the entry point, built via its constructor or
the fluent `GrabberBuilder` (`GrabberBuilder.cs`):

```
GrabAsync            search every capable ITorrentProvider, dedupe, parse, rank, return best + alternatives
SearchAsync          same, without ranking or filtering — the merged raw results
SearchRankedAsync     same, ranked and filtered (best first, ineligible removed) — every survivor, not just the best
GrabAndDownloadAsync  GrabAsync, then IDownloadClient.AddAsync on the winner
GrabAndResolveAsync   GrabAsync, then IDebridService.ResolveAsync on the winner
```

`GrabberOptions` (`GrabberOptions.cs`) controls provider timeout, parallel vs. sequential
querying, deduplication, whether to parse, an optional "quick grab" score threshold that
stops as soon as a candidate clears it, and `PreferCachedReleases` (floats
debrid-cached results to the top when the configured `IDebridService` implements
`ICachedAvailabilityChecker`).

**Providers** (`Providers/`): `DirectProviderBase.cs` is the template for a
single-tracker HTTP provider — three methods (build query, build URI, parse response)
handle the HTTP plumbing; `ExampleDirectProvider.cs` is a skeleton showing the shape.
`SearchQuery.cs` builds a consistent free-text query from a typed `MediaRequest`, and
`MagnetBuilder.cs` builds magnet URIs plus a shared default tracker list.
`Providers/Torznab/` (`TorznabProvider.cs`, `TorznabOptions.cs`,
`TorznabQueryBuilder.cs`, `TorznabFeedParser.cs`) is the built-in adapter for any
Torznab-speaking indexer manager — configure `TorznabOptions.BaseUrl` against a Prowlarr
or Jackett endpoint and it searches whatever that manager aggregates. `GrabberFactory.cs`
constructs one `TorznabProvider` per `ServerConfig.Indexers` entry with a parseable
`BaseUrl` (a blank or unparseable one is logged and skipped, not a startup failure).

**Parsing and ranking**: `Parsing/DefaultReleaseParser.cs` extracts resolution, source,
codecs, season/episode, year, language, group, and audio format from a release title by
regex, best-effort — unknown fields are left null rather than guessed.
`Ranking/DefaultTorrentRanker.cs` filters (downloadable, min seeders, size bounds,
banned terms, optional relevance match) then scores the survivors on quality signals
from the parsed info, with seeders as the tiebreaker. `GrabberFactory.cs` passes
`ServerConfig.Ranking` straight through as `GrabberOptions.Ranking`. Both
`IndexerSearchSource`'s search catalogs and `MetadataBackedVideoSource`'s browse
catalogs call `ITorrentGrabber.SearchRankedAsync` — search, dedupe, parse, then rank
and filter via the same `DefaultTorrentRanker` `GrabAsync` uses, just returning every
surviving candidate instead of only the best one — so `Ranking` filters and reorders
what both a search catalog and a browse resolution see. `SearchAsync` (the merged,
unranked results, ineligible candidates included) remains available on
`ITorrentGrabber` for a caller that wants the raw list instead.

**Debrid** (`Debrid/`): `IDebridService` implementations `RealDebridService`
(`RealDebrid/RealDebridService.cs`, options in `RealDebridOptions.cs`) and
`TorBoxService` (`TorBox/TorBoxService.cs`, options in `TorBoxOptions.cs`, also
implements `ICachedAvailabilityChecker` and `IDebridLibrary`) turn a grabbed release
into direct download links. `MagnetResolver.cs` produces a magnet link from a result's
magnet, info hash, or `.torrent` file, and `Torrents/TorrentInfo.cs` is a minimal
bencode reader that extracts a v1 BitTorrent info hash from raw `.torrent` bytes without
a BitTorrent library. `GrabberFactory.cs` builds one of these from `ServerConfig.Debrid`
— `Provider` selects which (`"torbox"` or `"realdebrid"`, case-insensitive); a missing
`ApiKey` or an unrecognized `Provider` is logged and skipped, leaving the server with no
debrid service rather than refusing to start. This is the same instance handed to
`Sources/ReleaseStreamResolver.cs` and to every plugin via `IServerServices.Debrid` — see
"Search, out of the box" above.

**Download clients** (`Download/`): `IDownloadClient` implementations
`QBittorrentClient` (`QBittorrent/QBittorrentClient.cs`, options in
`QBittorrentOptions.cs`) and `TransmissionClient` (`Transmission/TransmissionClient.cs`,
options in `TransmissionOptions.cs`) hand a release to a running qBittorrent or
Transmission instance. `GrabberFactory.cs` builds one of these from
`ServerConfig.DownloadClient` the same way it builds a debrid service, but nothing in the
host currently calls `GrabAndDownloadAsync` — no route hands a resolved release to a
download client yet.

**Selection and infrastructure**: `Selection/MediaFileMatcher.cs` narrows a multi-file
release's per-file links down to the one a request asked for (an episode out of a
season pack, a track out of an album). `Scraping/FileResolverCache.cs` is a
size-bounded, LRU-evicted on-disk `IResolverCache`. `Scraping/RemoteZip.cs` reads a
single member out of a remote ZIP over HTTP range requests — including nested zips —
without downloading the whole archive, using only `System.IO.Compression`.
`Http/RetryHandler.cs` is a `DelegatingHandler` that retries transient HTTP failures
(429/502/503/504, network errors) with exponential backoff and jitter, honoring
`Retry-After`. `Consoles/ConsoleCatalog.cs` is an unrelated factual table of retro game
consoles (names, aliases, ROM extensions) useful to an emulator front end.

Reachable through the addon protocol above via the `idx:` search catalogs (see "Search,
out of the box") and the `meta:` browse catalogs (see "Browse: the `meta:` catalogs"),
and directly by a plugin author who wants more than that — see
`EverythingBox.Server.Core.Tests/` for the library's own test coverage. What is NOT yet
reachable: a self-download fallback for an uncached release, and archive-packaged
releases — see Planned below.

## The cleanliness gate

`RepositoryCleanlinessTests` fails the build if a content source is named — in the
working tree or anywhere in git history. It runs in CI with full history fetched.

This repository ships the plugin host, not any source. That is a property worth
enforcing mechanically rather than by discipline: a name added and later deleted is
still public, so the history check matters more than the working-tree one.

Prowlarr and Jackett are allowlisted. They are indexer managers a user can point a
config `Indexers` entry or a plugin's `AddIndexer` at, not sources themselves, and
supporting that kind of infrastructure is the point.

## Planned — not yet implemented

Everything in this section describes where the project is going, not what exists in
this codebase today. None of it is callable, configurable, or partially wired up unless
explicitly noted. The indexer tier, the metadata tier, `IServerServices`, and the
`idx:`/`meta:` catalogs described above are no longer planned — they exist; what
remains is genuinely absent.

- **A MonoTorrent-backed self-download fallback** for an uncached debrid release.
  `Sources/ReleaseStreamResolver.cs` deliberately has none today: an uncached release
  returns a "still caching" notice, and retrying the same search later — once the debrid
  service finishes caching it on its own — is currently the only way to get a link.
- **A SharpCompress-backed archive reader and `ArchiveNormalizer`**, so an
  archive-packaged release (`.zip`/`.rar`/`.7z`) can be browsed and streamed
  member-by-member the way a single video/audio/document file already can.
- **Retrying a source whose search throws**, or a required-source / retry-until-deadline
  policy for `WarmUpAsync`. Today `WarmUpAsync` is a single best-effort call at startup
  (see the failure-containment table above) and a throwing `SearchAsync`/`ResolveAsync`/etc.
  simply degrades that one request to its route's empty shape — there is no retry.

The first two are host-side (`EverythingBox.Server`), not `EverythingBox.Server.Core`,
because each needs a package Core is not allowed to take.
