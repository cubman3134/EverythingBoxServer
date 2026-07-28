# EverythingBoxServer

A general-purpose media server for [EverythingBox](https://github.com/cubman3134/EverythingBox).
It finds releases through the indexers *you* configure, ranks them, resolves them to a
playable link, and serves them to the app over EverythingBox's addon protocol.

It ships the engine. It does not ship the sources.

## What that means

EverythingBoxServer talks to infrastructure you already run or pay for:

- **Indexers** — a Torznab adapter, so a single configured endpoint fronts Prowlarr,
  Jackett, and the whole \*arr indexer ecosystem. **No indexer ships configured.** The
  `Indexers` list is empty in a fresh install and stays empty until you fill it in.
- **Download clients** — qBittorrent and Transmission.
- **Debrid** — Real-Debrid and TorBox, acting on your own account.

And it ships the machinery to *build* a source — an HTTP/JSON/RSS provider base class, a
worked example, remote-zip range reads, nested-archive extraction, a resolver cache, an
alternate curl transport, and a bencode parser — without shipping a single site that
uses any of it.

Sources arrive as **plugins**. What you point it at is your decision and your
responsibility.

## Pipeline

```
MediaRequest
   │
   ▼
ITorrentProvider(s) ──► parallel search (per-provider timeout, errors captured)
   │
   ▼
dedupe (info hash → download URL → title+size)
   │
   ▼
IReleaseParser ──► resolution, source, codec, season/episode, year, group
   │
   ▼
ITorrentRanker ──► eligibility gate, additive scoring, seeders as tiebreak
   │
   ▼
resolve ──► debrid direct link, or self-download over BitTorrent, or hosted file
```

Ask for one episode out of a season pack, one track out of an album, or one file out of
a multi-file bundle, and `MediaFileMatcher` picks just that file — on Real-Debrid, before
the rest is ever cached.

## Plugins

Two tiers, so a plugin only implements as much as it needs.

**Indexers** implement `ITorrentProvider` and inherit the entire pipeline — dedupe,
parsing, ranking, cached-first ordering, debrid resolution, single-file extraction, and
the BitTorrent fallback — for free.

```csharp
public sealed class MyIndexer(HttpClient http) : DirectProviderBase(http)
{
    public override string Name => "MyIndexer";
    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportedMediaTypes = [MediaType.Movie, MediaType.Tv],
    };

    protected override string BuildSearchQuery(MediaRequest request) => /* … */;
    protected override Uri BuildRequestUri(string query, MediaRequest request) => /* … */;
    protected override IReadOnlyList<TorrentResult> ParseResponse(string body, MediaRequest request) => /* … */;
}
```

**Sources** implement `IMediaSource` when they don't fit the torrent pipeline at all —
they declare their own catalogs, run their own search, and resolve their own streams,
calling back into the server's services when useful.

```csharp
public interface IMediaSource
{
    string Key { get; }
    IReadOnlyList<CatalogDescriptor> Catalogs { get; }

    Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct);
    Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct);
    Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct);
}
```

Both are registered from a plugin entry point:

```csharp
public sealed class MyPlugin : IPlugin
{
    public string Key => "myplugin";
    public string DisplayName => "My Plugin";
    public Version ApiVersion => ServerApi.Version;

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        registry.AddIndexer(new MyIndexer(context.Http));
        registry.AddSource(new MySource(context));
    }
}
```

Drop the build output in `plugins/<key>/` and restart. Each plugin loads into its own
`AssemblyLoadContext`, gets a private cache directory and its own config section, and is
skipped with a logged error — rather than taking the server down — if it fails to load.

See `docs/ARCHITECTURE.md` for the full contract and
`EverythingBox.Server.SampleSource/` for a complete working plugin.

## Configuration

`everythingbox-server.json` (override the path with `EBS_CONFIG`):

```jsonc
{
  "Listen": "http://0.0.0.0:7000",
  "AccessToken": null,               // required if reachable from the internet
  "Debrid": { "Provider": "torbox", "ApiKey": "" },
  "DownloadClient": null,
  "Indexers": [],                    // Torznab endpoints — empty by default
  "Ranking": { "MinSeeders": 1, "BannedTerms": [] },
  "Download": { "MaxMB": 200, "TimeoutSeconds": 120, "Connections": 4 },
  "Plugins": { }                     // one opaque section per installed plugin
}
```

Set `AccessToken` before exposing the server to the internet. It becomes a URL path
prefix, so anyone without it cannot reach your debrid account.

## Building

```bash
dotnet build
dotnet test
```

Target framework .NET 9. The core library has no third-party dependencies — just the BCL.

## Legal

EverythingBoxServer ships no content and no content source. It searches the indexers you
configure, and hands results to the download client or debrid service you configure, on
accounts you control. You are responsible for ensuring your use complies with the terms
of those services and the laws of your jurisdiction.

MIT licensed. See [LICENSE](LICENSE).
