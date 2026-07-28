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
  "Plugins": { }                     // one opaque section per installed plugin, keyed by plugin Key
}
```

Any key not shown above is not read by anything — the config loader ignores unknown
properties rather than erroring, so a typo or a stale example silently does nothing.

Set `AccessToken` before exposing the server to the internet. It becomes a URL path
prefix (`/<token>/manifest.json` and friends), so anyone without it cannot reach your
server's routes at all.

## The torrent pipeline (`EverythingBox.Server.Core`)

Separate from the plugin/`IMediaSource` contract above, `EverythingBox.Server.Core` is a
standalone, dependency-free library for the search → parse → rank → resolve pipeline a
torrent-backed source needs. Nothing in the host or the plugin contract references it yet
— no shipped plugin constructs a `TorrentGrabber` — but it exists and is fully tested, so
a plugin author can use it today without waiting for the indexer tier described under
Planned.

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
  Prowlarr or Jackett endpoint via `TorznabOptions.BaseUrl` and it can search everything
  that manager aggregates. No provider is constructed by the host; you wire one up in
  your own plugin.
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
  album). `FileResolverCache` is a size-bounded on-disk `IResolverCache`. `RemoteZip`
  reads a single member out of a remote ZIP over HTTP range requests, including nested
  zips, without downloading the archive. `RetryHandler` is an `HttpClient`
  `DelegatingHandler` with exponential-backoff retry for transient failures.
- **Also included**: `ConsoleCatalog`, a factual table of retro game consoles (names,
  aliases, ROM extensions) useful to any emulator front end.

None of this is wired into the host or exposed over the addon protocol — it's a library
a plugin can build on, not a feature the server ships with. `EverythingBox.Server.Core`
takes no third-party package (see `CoreDependencyTests`); anything a piece of the
pipeline needs beyond the BCL — an archive library, a BitTorrent library — belongs in the
host or a plugin behind an interface instead.

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
in this section is installed, callable, or configurable today. `EverythingBox.Server.Core`
(above) is the pipeline library these will wire up to; none of it ships configured, and
no `Indexers`/`Debrid`/`DownloadClient`/`Ranking` config section exists yet.

- A torrent-indexer plugin tier — `IPluginRegistry.AddIndexer`, separate from the
  `IMediaSource` tier plugins use today — so an indexer plugin can plug into the shared
  pipeline instead of owning search end to end.
- A metadata-source contract (`IPluginRegistry.AddMetadata` / `IMetadataSource`) for
  movie/series browsing decoupled from any one indexer.
- `IServerServices` on `IPluginContext`, so a plugin can call back into shared pipeline
  services instead of implementing resolution itself.
- The `idx:` route in `SourceRouter`, and the generic `IndexerSearchSource` /
  `MetadataBackedVideoSource` sources built on top of it.
- Config sections (`Indexers`, `Debrid`, `DownloadClient`, `Ranking`, `Download`) so an
  indexer, debrid account, and download client can actually be configured on a running
  server.
