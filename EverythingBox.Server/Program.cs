using System.Net.Http.Headers;
using EverythingBox.Server;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core;
using EverythingBox.Server.Download;
using EverythingBox.Server.Plugins;
using EverythingBox.Server.Routing;
using EverythingBox.Server.Sources;

var config = ServerConfig.Load();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(config.Listen);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ManifestBuilder>();

// Registered as its own singleton — separately from the HttpClient built on top of it below —
// so GrabberFactory can wrap the SAME transport in RetryHandler for every indexer/debrid/
// download-client call, rather than those calls silently going out over an unrelated
// HttpClientHandler of GrabberFactory's own making. HttpClient does not expose whatever handler
// it was built with, so this is the only way to actually share one transport with it; see
// GrabberFactory.WrapWithRetry's doc comment for what broke before this existed (a test-installed
// fake handler on the shared HttpClient below was silently never consulted for any pipeline
// call — SearchToStreamTests is what caught it).
builder.Services.AddSingleton<HttpMessageHandler>(_ => new HttpClientHandler());

builder.Services.AddSingleton(sp =>
{
    var http = new HttpClient(sp.GetRequiredService<HttpMessageHandler>(), disposeHandler: false)
    {
        Timeout = TimeSpan.FromSeconds(60),
    };
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EverythingBoxServer", "1.0"));
    return http;
});

builder.Services.AddSingleton<PluginHost>();

builder.Services.AddSingleton(_ => new FileCache(config.ResolvedFilesCacheDir));

builder.Services.AddSingleton(sp =>
{
    var host = sp.GetRequiredService<PluginHost>();
    var loggers = sp.GetRequiredService<ILoggerFactory>();
    var http = sp.GetRequiredService<HttpClient>();
    var transport = sp.GetRequiredService<HttpMessageHandler>();
    var files = sp.GetRequiredService<FileCache>();
    var cacheRoot = Path.Combine(config.ResolvedFilesCacheDir, "plugins");
    var log = loggers.CreateLogger<SourceRouter>();

    // Debrid depends only on config (never on what a plugin registers), so it can be built
    // up front and handed to every plugin's IServerServices.Debrid as-is. Build it once here
    // and pass it to both the plugin system and the grabber below, ensuring there is exactly
    // one debrid instance for the entire process.
    var debrid = GrabberFactory.BuildDebrid(config, http, loggers, transport);

    // Plugins register indexers during Configure, but Configure is also where a plugin
    // receives IServerServices — which holds the grabber built FROM those indexers. Handing
    // out a real, eagerly-built grabber here would mean building it before any plugin has
    // registered anything. DeferredTorrentGrabber breaks the cycle: a plugin may stash the
    // ITorrentGrabber reference during registration and call it later while serving a
    // request (see IServerServices.Grabber for the rule stated where a plugin author will
    // see it) — but calling it FROM Configure throws immediately instead of silently
    // building and permanently caching a grabber with zero indexers. SetGrabber below is
    // called exactly once, after host.Load returns, once every plugin's indexers are known.
    var deferredGrabber = new DeferredTorrentGrabber();
    var services = new ServerServices(deferredGrabber, debrid, files);

    var plugins = host.Load(
        config.ResolvedPluginsDirectory,
        plugin => new PluginContext(plugin.Key, config, loggers, http, cacheRoot, services));

    // Now that every plugin's Configure has run, every indexer is known — build the real
    // grabber (config indexers + plugin indexers, one merged provider list) and bind it.
    // Pass the pre-built debrid so it's the same instance everywhere.
    var pluginIndexers = plugins.SelectMany(p => p.Indexers).ToList();

    // At most one provider tracker applies across the whole server (it orders ONE provider
    // list) — PluginRegistry.AddProviderTracker already refuses a SECOND registration
    // within one plugin's own Configure. Two DIFFERENT plugins each successfully
    // registering their own tracker is a conflict PluginRegistry cannot see (each plugin
    // gets a fresh registry), so it's resolved here: the first plugin in load order wins,
    // and every later one is logged and dropped rather than silently overriding it.
    var providerTrackers = plugins.Where(p => p.ProviderTracker is not null).ToList();
    if (providerTrackers.Count > 1)
    {
        log.LogWarning(
            "{Count} plugins each registered a provider tracker ({Keys}); only '{Winner}' (first in load order) is used.",
            providerTrackers.Count, string.Join(", ", providerTrackers.Select(p => p.Key)), providerTrackers[0].Key);
    }
    var providerTracker = providerTrackers.Count > 0 ? providerTrackers[0].ProviderTracker : null;

    var grabber = GrabberFactory.Build(config, http, pluginIndexers, loggers, debrid, transport, providerTracker);
    deferredGrabber.SetGrabber(grabber);

    log.LogInformation(
        "Torrent pipeline ready: {Total} indexer(s) ({Configured} from config, {FromPlugins} from plugins); debrid: {Debrid}",
        grabber.Providers.Count, config.Indexers.Count, pluginIndexers.Count, debrid?.Name ?? "none");

    // IndexerSearchSource is what makes a stock server useful without installing any
    // plugin: it exposes every configured indexer (config + plugins, already merged into
    // `grabber` above) as search-only catalogs. It is built with `deferredGrabber`, not
    // the concrete `grabber`, because SearchAsync/ResolveAsync run later, while serving a
    // request — by then SetGrabber above has bound the real grabber; calling through the
    // deferred wrapper during THIS factory would throw by design.
    //
    // The self-download fallback is opt-in and OFF by default (see ServerConfig.Download):
    // only construct the downloader — and the BitTorrent engine setup that goes with it —
    // when it will actually be used. MonoTorrentDownloader itself allocates nothing at
    // construction (it just stores the logger/HttpClient), but there's no reason to hold
    // even that for a server that will never call it; a disabled config should be as inert
    // as if this line weren't here at all. Built over the SAME shared HttpClient as every
    // other outbound call, so a .torrent-to-magnet fetch inherits its retry handling.
    ITorrentDownloader? downloader = config.Download.Enabled
        ? new MonoTorrentDownloader(loggers.CreateLogger<MonoTorrentDownloader>(), http)
        : null;

    // ReleaseStreamResolver is constructed exactly once, right here, because this whole
    // factory lambda is itself only ever invoked once (AddSingleton below caches the
    // result) — so its constructor's "no debrid configured" log line fires exactly once,
    // never once per request.
    //
    // Appended AFTER every plugin source so a plugin can never be shadowed by it: if a
    // plugin also declares key "idx", SourceRouter's duplicate-key handling keeps the
    // first registration (the plugin's) and logs+drops this one instead.
    var resolver = new ReleaseStreamResolver(
        debrid, loggers.CreateLogger<ReleaseStreamResolver>(), downloader, files, config.Download);
    var indexerSource = new IndexerSearchSource(deferredGrabber, resolver, grabber.Providers.Count, loggers.CreateLogger<IndexerSearchSource>());

    // MetadataBackedVideoSource turns every metadata source collected across all loaded
    // plugins (there is no config-driven equivalent — metadata always comes from a
    // plugin) into browsable movie/series shelves. Same reasoning as indexerSource above:
    // built with `deferredGrabber` because SearchAsync/ResolveAsync run later, and
    // appended AFTER every plugin source (alongside indexerSource) so a plugin can never
    // be shadowed by it.
    var pluginMetadata = plugins.SelectMany(p => p.MetadataSources).ToList();
    var metadataSource = new MetadataBackedVideoSource(
        pluginMetadata, deferredGrabber, resolver, loggers.CreateLogger<MetadataBackedVideoSource>());

    var sources = plugins.SelectMany(p => p.Sources).Append(indexerSource).Append(metadataSource);
    return new SourceRouter(sources, log);
});

var app = builder.Build();

var log = app.Services.GetRequiredService<ILogger<Program>>();

// A secret token becomes a URL path prefix, so the server can sit on a public port
// without exposing it to anyone who finds the URL. The client strips "/manifest.json"
// to derive the base, so it carries the prefix onto every later call automatically.
var token = config.AccessToken?.Trim();
var prefix = string.IsNullOrEmpty(token) ? "" : "/" + token;

if (string.IsNullOrEmpty(token))
    log.LogWarning("No access token set. Fine on a trusted LAN — do NOT port-forward this without one.");
else
    log.LogInformation("Access token set; the addon is served under a token-prefixed path.");

app.Use(async (ctx, next) =>
{
    var started = System.Diagnostics.Stopwatch.StartNew();
    await next();
    started.Stop();

    var path = ctx.Request.Path.Value ?? "";
    // ASP.NET's routing matches literal segments case-insensitively, so "/TOKEN/manifest.json"
    // reaches the same route as "/token/manifest.json" — the redaction has to match that or a
    // differently-cased request leaks the token into the log in plaintext (this was already
    // fixed once elsewhere in this file as a Critical; do not regress it here too).
    if (!string.IsNullOrEmpty(token)) path = path.Replace("/" + token, "/<token>", StringComparison.OrdinalIgnoreCase);

    log.LogInformation("{Method} {Path}{Query} -> {Status} ({Ms} ms)",
        ctx.Request.Method, path, ctx.Request.QueryString.Value, ctx.Response.StatusCode, started.ElapsedMilliseconds);
});

app.MapGet("/", () => Results.Text(
    "EverythingBox Server.\n" +
    "Add this in the app via Add-ons -> Add addon by URL using:\n" +
    "    <this URL>/<token>/manifest.json   (the token, if configured)\n", "text/plain"));

app.MapGet("/health", () => Results.Json(new { ok = true }));

app.MapBrowse(prefix);
app.MapStreams(prefix);
app.MapFiles(prefix);

// Force plugin loading now, so failures surface at startup rather than on first request.
var sourceRouter = app.Services.GetRequiredService<SourceRouter>();

// Give every registered source one chance to cache expensive state before the server takes
// traffic. This is deliberately NOT retry-until-deadline or "required source" behavior —
// that belongs to a later milestone once a real source needs it. A source's WarmUpAsync is
// plugin-authored code, same as everything else it implements, so it gets the same
// containment: a throw (or a Failed result) is a logged warning, never fatal, and never
// blocks another source's warm-up. A HANG gets the same treatment via SourceWarmUp's bound
// timeout — a throw is not the only way plugin code can fail to cooperate.
foreach (var source in sourceRouter.Sources)
{
    await SourceWarmUp.RunAsync(source, PluginDiagnostics.SafeLabel(source), log, SourceWarmUp.DefaultTimeout);
}

log.LogInformation("Listening on {Urls}", config.Listen);
app.Run();

// Exposed so WebApplicationFactory-style tests can reference it.
public partial class Program;
