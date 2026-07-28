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

## Building

```bash
dotnet build
dotnet test
```

Target framework .NET 9. `EverythingBox.Server.Abstractions` — the only assembly a
plugin references — takes exactly one dependency, `Microsoft.Extensions.Logging.Abstractions`.
The host project is an ASP.NET Core web app and carries the usual ASP.NET Core dependencies.

## Legal

EverythingBoxServer ships no content and no content source. What a running server can
find and serve depends entirely on the plugins you install and what you point them at.
You are responsible for ensuring your use complies with the terms of any service you
configure and the laws of your jurisdiction.

MIT licensed. See [LICENSE](LICENSE).

## Planned

The pieces below don't exist yet. They're the direction, not the current state — nothing
in this section is installed, callable, or configurable today.

- A torrent-indexer plugin tier (a Torznab adapter fronting indexer managers like Prowlarr
  and Jackett), separate from the `IMediaSource` tier plugins use today.
- A search → parse → rank pipeline over indexer results, with a resolver that picks a
  single file out of a multi-file release.
- Debrid integration (e.g. Real-Debrid, TorBox) and download-client integration (e.g.
  qBittorrent, Transmission) as resolution targets for that pipeline.
- A metadata-source contract for movie/series browsing decoupled from any one indexer.
- Additional `IPluginRegistry` registration methods and a richer `IPluginContext` for
  plugins that want to reuse pipeline services instead of owning their own resolution
  end to end.
