using System.Net.Http.Headers;
using EverythingBox.Server;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core;
using EverythingBox.Server.Plugins;
using EverythingBox.Server.Routing;
using EverythingBox.Server.Sources;

var config = ServerConfig.Load();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(config.Listen);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ManifestBuilder>();

builder.Services.AddSingleton(_ =>
{
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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
    var files = sp.GetRequiredService<FileCache>();
    var cacheRoot = Path.Combine(config.ResolvedFilesCacheDir, "plugins");
    var log = loggers.CreateLogger<SourceRouter>();

    // Debrid depends only on config (never on what a plugin registers), so it can be built
    // up front and handed to every plugin's IServerServices.Debrid as-is. Build it once here
    // and pass it to both the plugin system and the grabber below, ensuring there is exactly
    // one debrid instance for the entire process.
    var debrid = GrabberFactory.BuildDebrid(config, http, loggers);

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
    var grabber = GrabberFactory.Build(config, http, pluginIndexers, loggers, debrid);
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
    // ReleaseStreamResolver is constructed exactly once, right here, because this whole
    // factory lambda is itself only ever invoked once (AddSingleton below caches the
    // result) — so its constructor's "no debrid configured" log line fires exactly once,
    // never once per request.
    //
    // Appended AFTER every plugin source so a plugin can never be shadowed by it: if a
    // plugin also declares key "idx", SourceRouter's duplicate-key handling keeps the
    // first registration (the plugin's) and logs+drops this one instead.
    var resolver = new ReleaseStreamResolver(debrid, loggers.CreateLogger<ReleaseStreamResolver>());
    var indexerSource = new IndexerSearchSource(deferredGrabber, resolver, loggers.CreateLogger<IndexerSearchSource>());

    var sources = plugins.SelectMany(p => p.Sources).Append(indexerSource);
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
