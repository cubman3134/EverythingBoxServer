# EverythingBoxServer

A plugin host for [EverythingBox](https://github.com/cubman3134/EverythingBox). It loads
media-source plugins, exposes them over EverythingBox's addon protocol (manifest, catalog,
detail, stream), and serves whatever those plugins hand back.

It ships the host. It does not ship any source.

## What that means

EverythingBoxServer on its own has no built-in source of media. Everything a running
server can browse, search, or stream comes from a plugin you install — the
`plugins/<key>/` folder is empty in a fresh checkout, and the server runs fine with
nothing in it; it just has nothing to serve.

Sources arrive as **plugins**. What you point one at is your decision and your
responsibility.

## Plugins

A plugin is one assembly with a public, parameterless-constructible `IPlugin`:

```csharp
public sealed class MyPlugin : IPlugin
{
    public string Key => "myplugin";           // namespaces this plugin's ids and config section
    public string DisplayName => "My Plugin";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        registry.AddSource(new MySource(context));
    }
}
```

`Configure` registers one or more `IMediaSource`s — each one owns its own catalogs, its
own search, and its own stream resolution:

```csharp
public interface IMediaSource
{
    string Key { get; }
    IReadOnlyList<CatalogDescriptor> Catalogs { get; }

    Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct);
    Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct);
    Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct);

    // Optional — implement only what you need.
    Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct);
    Task<WarmUpResult> WarmUpAsync(CancellationToken ct);
}
```

`IPluginContext` gives a plugin a logger factory, a shared `HttpClient` the host owns
(do not dispose it), a plugin-private cache directory created before `Configure` runs,
and its own section of the server config via `GetConfig<T>()`.

Drop the build output in `plugins/<key>/` and restart. Each plugin loads into its own
`AssemblyLoadContext` so its dependencies cannot collide with another plugin's or the
host's, gets a private cache directory and its own config section, and is skipped with a
logged error — rather than taking the server down — if it fails to load, declares an
incompatible `ApiVersion`, or throws while registering.

**One rule for every plugin project:** reference `EverythingBox.Server.Abstractions` with
`Private="false"`. The host already supplies that assembly at runtime; copying a second
copy into a plugin's own output folder loads it twice, and every cast between the host's
`IMediaSource` and the plugin's stops working because, to the runtime, they're now two
unrelated types with the same name.

See `docs/ARCHITECTURE.md` for the full contract and
`EverythingBox.Server.SampleSource/` for a complete working plugin — it scans a
configured folder and serves the files it finds.

## Configuration

`everythingbox-server.json` next to the executable (override the path with `EBS_CONFIG`):

```jsonc
{
  "Listen": "http://0.0.0.0:7000",
  "AccessToken": null,               // required if reachable from the internet
  "PluginsDirectory": null,          // defaults to "plugins" next to the executable
  "FilesCacheDir": null,             // defaults to "files" next to the executable
  "Manifest": {
    "Id": "com.everythingbox.server",
    "Name": "EverythingBox Server",
    "Version": "1.0.0",
    "Description": "Media from the sources you configure.",
    "Accent": "#3E8E7E"
  },
  "Plugins": { },                    // one opaque section per installed plugin, keyed by plugin Key

  "Indexers": [                      // Torznab endpoints (e.g. Prowlarr or Jackett). Ships EMPTY.
    { "Name": "My Prowlarr", "BaseUrl": "http://localhost:9696/1/api", "ApiKey": "..." }
  ],
  "Debrid": null,                    // { "Provider": "torbox" | "realdebrid", "ApiKey": "..." }
  "DownloadClient": null,            // { "Kind": "qbittorrent" | "transmission", "BaseUrl": "...", "Username": "...", "Password": "...", "Category": "..." }
  "Download": {                      // self-download fallback for an uncached release — OFF by default
    "Enabled": false,                //   fetch it over BitTorrent from THIS host's own IP instead of waiting on debrid
    "MaxSizeMB": 2048,               //   never self-download a release larger than this — or one of unknown size
    "TimeoutSeconds": 600            //   give up on a stalled/seedless swarm instead of holding the request open
  },
  "Ranking": { }                     // MinSeeders, MinSizeBytes, MaxSizeBytes, PreferredResolutions,
                                      // PreferredAudioFormats, PreferredLanguages, PreferredSubtitleLanguages,
                                      // BannedTerms, RequireRelevanceMatch — see RankingOptions for defaults
}
```

Any key not shown above is not read by anything — the config loader ignores unknown
properties rather than erroring, so a typo or a stale example silently does nothing.
`Indexers` ships empty and `Debrid`/`DownloadClient` ship unset — a fresh checkout
searches and streams nothing until you configure at least one indexer, the same way it
installs no plugin.

Every entry degrades rather than refuses to start: a blank/unparseable indexer
`BaseUrl`, a `Debrid` block with no `ApiKey`, or an unrecognized `Debrid.Provider` /
`DownloadClient.Kind` is logged and skipped (`GrabberFactory.cs`) — a typo in one line
does not stop the server from booting with everything else it understood.

Set `AccessToken` before exposing the server to the internet. It becomes a URL path
prefix (`/<token>/manifest.json` and friends), so anyone without it cannot reach your
server's routes at all.

### Search, out of the box

Configuring at least one `Indexers` entry makes the server searchable without
installing any plugin. The host registers a built-in `idx:` source
(`IndexerSearchSource`) exposing one search-only catalog per media type the pipeline
understands — movies, series, music, audiobooks, books, comics — backed by every
configured indexer (plus any a plugin registers, see `AddIndexer` below), but only once
at least one indexer exists. With `Indexers: []` and no plugin-registered indexer, the
manifest declares none of these catalogs at all, rather than advertising six shelves
that can never return anything.

Add `Debrid` to make a search result playable: resolving a release asks the configured
debrid service (TorBox or Real-Debrid today) for a direct link. If the release isn't
already cached on the debrid service, the server returns a "still caching, try again
shortly" notice — unless the self-download fallback below is switched on.

#### The self-download fallback (`Download`)

When debrid reports a release as still caching, the server can fetch it itself instead of
handing back the notice — `ReleaseStreamResolver` joins the swarm via
`MonoTorrentDownloader` (an `ITorrentDownloader`), saves the file into `FilesCacheDir`, and
serves it back as a hosted `files/...` URL the client plays directly.

**This is off by default, and turning it on is a network-exposure decision, not a speed
setting.** Every other path here talks only to services you configured (an indexer, a
debrid provider). This one joins a BitTorrent swarm **from the host's own IP address** to
download the release, which is a different thing to opt into. It exists for the case where
waiting on debrid to cache a release isn't acceptable and fetching it yourself is.

`Download` controls it:

- **`Enabled`** — `false` by default. Nothing is ever self-downloaded until you set this
  `true`.
- **`MaxSizeMB`** — releases larger than this are never fetched (default `2048`), so a
  fallback finishes while you're still interested. **A release whose size the indexer
  didn't report is treated as over the cap and never fetched** — the point of the cap is to
  refuse an unbounded download, and "we don't know how big it is" is exactly that.
- **`TimeoutSeconds`** — a stalled or seedless swarm is given up on after this long
  (default `600`) and the request falls back to the caching notice, rather than being held
  open indefinitely.

Concurrent viewers of the same still-caching release share one download rather than each
starting their own; a timed-out or empty attempt is not memoized, so a later retry can
still succeed.

`DownloadClient` (qBittorrent or Transmission) feeds the same pipeline but is not
exercised by anything the host calls today — no route hands a release to a download
client yet. `Ranking` (seeders, size bounds, preferred resolution/language/format,
banned terms) has no such gap: it's applied to both the single-best ranked path
(`GrabAsync`) and the search catalogs described above, via `ITorrentGrabber.SearchRankedAsync`
— search, dedupe, parse, then rank and filter, best first, ineligible releases
removed per `Ranking`.

### Browse, with a metadata plugin

Search is not the only way in. `IPluginRegistry.AddMetadata` registers an
`IMetadataSource` (`EverythingBox.Server.Abstractions/Metadata/IMetadataSource.cs`),
decoupled from any one indexer:

```csharp
public interface IMetadataSource
{
    string Name { get; }
    IReadOnlyList<string> SupportedMediaTypes { get; }   // protocol strings — "movie", "series"

    Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct);

    // Optional — defaults to empty, so a movie-only source need not implement it.
    Task<IReadOnlyList<MetadataEpisode>> EpisodesAsync(string seriesId, CancellationToken ct);
}
```

`MetadataItem` carries an id, title, media type, and optionally a year and poster URL
— `MediaType` is the protocol string this item actually is ("movie", "series"), and
the host shelves it by that, not by whichever catalog it was returned from, so a
source mixing types into one `BrowseAsync` call gets each item shelved correctly (or
dropped, if it doesn't match). `MetadataEpisode` carries a season, episode, title, and
optionally an overview — no id of its own; nothing downstream needs one.

The host's built-in `meta:` source (`Sources/MetadataBackedVideoSource.cs`) pairs
every registered `IMetadataSource` with the same indexer/debrid pipeline `idx:` uses:
browsing `meta:movies`/`meta:series` lists titles from the metadata source(s); opening
a series (`/detail/series/{id}.json`) expands it into episodes via `EpisodesAsync`. A
bare series id has no single release to resolve — only a movie or one of its episodes
does; resolving either searches the pipeline (`SearchRankedAsync`, the same ranked path
described above) for a matching release, then hands it to the same `Debrid`-backed
resolution `idx:` uses. **The metadata source only supplies what to browse; the
`Indexers`/`Debrid` config above is still what makes a browsed title actually playable.**

Same "advertise only what can be filled" discipline as `idx:`: `meta:movies` is
declared only when a registered metadata source's `SupportedMediaTypes` includes
"movie" (independently for `meta:series`/"series"). **With no metadata plugin
installed, neither catalog appears — the server ships no metadata source, the same
way it ships no indexer.**

## The torrent pipeline (`EverythingBox.Server.Core`)

`EverythingBox.Server.Core` is a standalone, dependency-free library for the search →
parse → rank → resolve pipeline a torrent-backed source needs. The host
(`EverythingBox.Server`) wires it up from the `Indexers`/`Debrid`/`DownloadClient`/
`Ranking` config above via `GrabberFactory` and exposes it as the `idx:` search catalogs
described above — see "Search, out of the box". A plugin can also build directly on the
library itself, same as before this wiring existed:

```csharp
var grabber = new GrabberBuilder()
    .AddProvider(new TorznabProvider(httpClient, new TorznabOptions { BaseUrl = ... }))
    .UseDownloadClient(new QBittorrentClient(httpClient, new QBittorrentOptions { BaseUrl = ... }))
    .Build();

var result = await grabber.GrabAndDownloadAsync(new MovieRequest { Title = "..." });
```

- `TorrentGrabber` (`GrabberBuilder` for fluent setup) queries every capable
  `ITorrentProvider` in parallel, deduplicates results by info hash, runs them through
  `DefaultReleaseParser` (resolution, source, codecs, season/episode, etc.), and picks
  the best via `DefaultTorrentRanker`. `GrabberOptions` controls timeouts, dedup,
  parsing, and an optional "quick grab" score threshold that stops early.
- **Providers**: `DirectProviderBase` is the template for a single-tracker HTTP
  provider (three methods: build query, build URI, parse response). `TorznabProvider`
  is the built-in adapter for any Torznab-speaking indexer manager — point it at a
  Prowlarr or Jackett endpoint via `TorznabOptions.BaseUrl` (or the `Indexers` config
  key above) and it can search everything that manager aggregates.
- **Debrid**: `IDebridService` implementations `RealDebridService` and `TorBoxService`
  (under `EverythingBox.Server.Core/Debrid/`) turn a grabbed release into direct
  download links via `GrabAndResolveAsync`. `MagnetResolver` and the bundled bencode
  parser (`TorrentInfo`) let a `.torrent`-only result be treated like a magnet one, with
  no BitTorrent library dependency.
- **Download clients**: `IDownloadClient` implementations `QBittorrentClient` and
  `TransmissionClient` (under `EverythingBox.Server.Core/Download/`) hand a grabbed
  release to a running qBittorrent or Transmission instance via
  `GrabAndDownloadAsync`.
- **Selection and caching**: `MediaFileMatcher` narrows a multi-file release down to
  the one file a request asked for (an episode out of a season pack, a track out of an
  album). `RetryHandler` is an `HttpClient` `DelegatingHandler` with exponential-backoff
  retry for transient failures.

`EverythingBox.Server.Core` still takes no third-party package (see
`CoreDependencyTests`); anything the pipeline needs beyond the BCL — an archive
library, a BitTorrent library — belongs in the host or a plugin behind an interface
instead, which is exactly how the host itself (`EverythingBox.Server.csproj`, an ASP.NET
Core project) is allowed to take on the ASP.NET Core dependencies Core cannot.

### Registering an indexer from a plugin

Alongside `AddSource`, `IPluginRegistry` has a second, smaller registration tier:

```csharp
void AddIndexer(ITorrentProvider provider);
```

An indexer registered this way is merged with every config-defined `Indexers` entry
into the same shared pipeline — it inherits dedupe, parsing, ranking, and resolution for
free rather than implementing search end to end the way an `IMediaSource` does. Reach
the resulting pipeline (and the configured debrid service) from any plugin via
`IPluginContext.Server`:

```csharp
public interface IServerServices
{
    ITorrentGrabber Grabber { get; }   // do not call from Configure — see the interface's own doc comment
    IDebridService? Debrid { get; }
    IFileCache Files { get; }
}
```

### Registering a metadata source from a plugin

A third, independent registration:

```csharp
void AddMetadata(IMetadataSource metadata);
```

A metadata source only supplies what to browse — the built-in `MetadataBackedVideoSource`
pairs it with the shared indexer/debrid pipeline automatically (see "Browse, with a
metadata plugin" above). A plugin registering one does not need to also register an
indexer or a source; the host already has a pipeline to search once `Indexers`/`Debrid`
are configured.

### Registering a provider tracker from a plugin

A fourth registration tunes the shared pipeline rather than adding to it:

```csharp
void AddProviderTracker(IProviderPerformanceTracker tracker);
```

An `IProviderPerformanceTracker` learns which indexers actually pay off. Before each
search the grabber calls `Prioritize` to order providers best-first (so a "quick grab"
score threshold can finish sooner), and after each search it calls `Record` with one
`ProviderOutcome` per provider (result count, whether it errored, how long it took,
whether it produced the winning release) — an implementation persists those stats so the
ordering improves across runs. **At most one applies across the whole server**: a single
plugin's registry rejects a second registration, and if two different plugins each
register one, the first in load order wins and the rest are logged and dropped
(`Program.cs`).

## Deploying

See [docs/DEPLOY.md](docs/DEPLOY.md) for how to publish the server, lay out a plugin's
build output under `plugins/<key>/`, point it at a config file, and keep it running.

## Building

```bash
dotnet build
dotnet test
```

Target framework .NET 9. `EverythingBox.Server.Abstractions` — the only assembly a
plugin references — takes exactly one dependency, `Microsoft.Extensions.Logging.Abstractions`.
`EverythingBox.Server.Core` takes none — BCL only, enforced by a test. The host project
is an ASP.NET Core web app and carries the usual ASP.NET Core dependencies.

## Legal

EverythingBoxServer ships no content and no content source. What a running server can
find and serve depends entirely on the plugins you install and what you point them at.
You are responsible for ensuring your use complies with the terms of any service you
configure and the laws of your jurisdiction.

MIT licensed. See [LICENSE](LICENSE).

## Planned

The pieces below don't exist yet. They're the direction, not the current state — nothing
in this section is installed, callable, or configurable today.

- A SharpCompress-backed archive reader and `ArchiveNormalizer`, so an archive-packaged
  release (`.zip`/`.rar`/`.7z`) can be browsed and streamed member-by-member the way a
  single video/audio/document file already can.

This is host-side (`EverythingBox.Server`), not `EverythingBox.Server.Core`, because it
needs a package Core is not allowed to take — the same reason the now-shipped
MonoTorrent-backed self-download fallback (see `Download` above) lives there.
