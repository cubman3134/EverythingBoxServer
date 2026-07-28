# Architecture

EverythingBoxServer is a plugin host. It knows how to load a plugin, route a request to
the source that owns it, and serve the addon protocol EverythingBox speaks. It does not
know where any media comes from — that arrives entirely as plugins.

## Projects

```
EverythingBox.Server.Abstractions/   the only assembly plugins reference
  IPlugin, IPluginRegistry, IPluginContext         (Abstractions/IPlugin.cs)
  IMediaSource                                     (Abstractions/IMediaSource.cs)
  CatalogDescriptor, MediaTypeDescriptor, CatalogItem, SourceCatalog,
  SourceStream, SourceContext, ProxyResponse, WarmUpResult             (Abstractions/Catalog.cs)
  ServerApi                                        (Abstractions/ServerApi.cs)

EverythingBox.Server/                the host
  Program.cs                          composition root and route mounting
  Plugins/PluginHost, PluginRegistry, PluginContext, PluginLoadContext
  Routing/SourceRouter
  AddonEndpoints                      manifest/catalog/detail/meta/stream/proxy/files routes
  ManifestBuilder, SafeUrlGuard, FileCache, ServerConfig

EverythingBox.Server.SampleSource/   a complete example plugin: a local folder
EverythingBox.Server.Tests/          xUnit tests for the host and Abstractions
tests/TestPlugin.Good, TestPlugin.Bad, TestPlugin.Dup
                                      fixture plugins PluginHostTests loads to exercise
                                      the success/failure/duplicate-key paths
```

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
}

public interface IPluginContext
{
    ILoggerFactory Loggers { get; }
    HttpClient Http { get; }              // shared; the host owns its lifetime, plugins must not dispose it
    string CacheDirectory { get; }        // plugin-private, created before Configure runs
    T? GetConfig<T>() where T : class;    // this plugin's own config section, or null if absent
}
```

There is one registration tier today: `IMediaSource`. A source owns its own catalogs,
its own search, and its own stream resolution end to end — there is no separate
"indexer" tier and no shared pipeline a source plugs into.

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

Zero. The host has no built-in `IMediaSource`. `EverythingBox.Server.SampleSource` is a
separate, optional plugin project — `LocalFolderSource` — that ships as a worked
example: it scans the folders in its config, lists media files it recognizes by
extension, and serves them through `OpenAsync`/the proxy route. It is not loaded by the
host unless its build output is placed under `plugins/local/` like any other plugin.

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
| `GET /detail/{type}/{id}.json` | `IMediaSource.DetailAsync` |
| `GET /meta/{type}/{id}.json` | Always an empty object — no metadata source exists yet |
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

## The cleanliness gate

`RepositoryCleanlinessTests` fails the build if a content source is named — in the
working tree or anywhere in git history. It runs in CI with full history fetched.

This repository ships the plugin host, not any source. That is a property worth
enforcing mechanically rather than by discipline: a name added and later deleted is
still public, so the history check matters more than the working-tree one.

Prowlarr and Jackett are allowlisted. They are indexer managers a user could point a
future plugin at, not sources themselves, and supporting that kind of infrastructure is
the point.

## Planned — not yet implemented

Everything in this section describes where the project is going, not what exists in
this codebase today. None of it is callable, configurable, or partially wired up unless
explicitly noted.

- **A torrent-indexer plugin tier**, likely something like `AddIndexer`/`ITorrentProvider`
  on `IPluginRegistry`, feeding a shared search → parse → rank pipeline instead of each
  plugin owning search end to end the way `IMediaSource` does.
- **A Torznab adapter** so a single configured endpoint fronts indexer managers such as
  Prowlarr and Jackett.
- **Debrid and download-client integration** (for example Real-Debrid, TorBox,
  qBittorrent, Transmission) as resolution targets for that pipeline, on accounts the
  user configures.
- **A metadata-source tier** (`AddMetadata`/`IMetadataSource`) for movie/series browsing
  decoupled from any one indexer.
- **A richer `IPluginContext`**, likely an `IServerServices Server` member, so a plugin
  can call back into shared pipeline services instead of implementing resolution itself.
- **Retrying a source whose search throws**, or a required-source / retry-until-deadline
  policy for `WarmUpAsync`. Today `WarmUpAsync` is a single best-effort call at startup
  (see the failure-containment table above) and a throwing `SearchAsync`/`ResolveAsync`/etc.
  simply degrades that one request to its route's empty shape — there is no retry.
