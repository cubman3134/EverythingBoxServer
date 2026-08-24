using System.Net.Http.Headers;
using EverythingBox.Server;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core;
using EverythingBox.Server.Download;
using EverythingBox.Server.Plugins;
using EverythingBox.Server.Routing;
using EverythingBox.Server.Sources;
using EverythingBox.Server.Subsonic;
using EverythingBox.Server.Sync;

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

// The sync object-store singleton exists only when the opt-in Sync config is enabled, so a
// default server constructs nothing and MapSync below is never mapped.
if (config.Sync.Enabled)
    builder.Services.AddSingleton(_ => new SyncStore(
        config.Sync.ResolvedSyncDirectory, config.Sync.PerNamespaceQuotaBytes, config.Sync.MaxObjectBytes));

// The self-download fallback is opt-in and OFF by default (see ServerConfig.Download).
// Register the downloader in DI — rather than newing it inline inside the SourceRouter
// factory below — only when it will actually be used, so a disabled config stays exactly as
// inert as before (nothing constructed, GetService returns null). Resolving it from DI is
// also what lets a test host swap in a fake via ConfigureTestServices, the same seam the
// shared HttpMessageHandler above is replaced through. MonoTorrentDownloader allocates
// nothing at construction; the guard is about intent, not cost. Built over the SAME shared
// HttpClient as every other outbound call, so a .torrent-to-magnet fetch inherits its retry.
if (config.Download.Enabled)
{
    builder.Services.AddSingleton<ITorrentDownloader>(sp => new MonoTorrentDownloader(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<MonoTorrentDownloader>(),
        sp.GetRequiredService<HttpClient>()));
}

// At most one plugin registers a music library. Plugins load lazily inside the SourceRouter
// factory below (its body runs post-Build, when the router is first resolved), so the library
// cannot be handed to builder.Services from in there. This closure carries the captured value
// out to the IMusicLibrary registration below, which forces the SourceRouter factory to run
// first — the same "load plugins once, then read what they registered" shape as the indexer,
// metadata and provider-tracker readbacks in the factory.
IMusicLibrary? loadedMusicLibrary = null;
// Every romhack source every plugin registered. Unlike the music library, MANY apply at once:
// a game's hacks are fanned out across all of them. Captured here for the DI registration below,
// for the same reason -- resolving SourceRouter is what forces plugin loading.
IReadOnlyList<IRomhackSource> loadedRomhackSources = Array.Empty<IRomhackSource>();
// Every homebrew source every plugin registered, for the same reason and read back the same way:
// a console's homebrew is fanned out across all of them, and resolving SourceRouter is what forces
// plugin loading.
IReadOnlyList<IHomebrewSource> loadedHomebrewSources = Array.Empty<IHomebrewSource>();

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
    var debridWait = TimeSpan.FromSeconds(Math.Max(0, config.Debrid?.WaitSeconds ?? 0));
    var services = new ServerServices(deferredGrabber, debrid, files, http, loggers, transport, debridWait);

    var plugins = host.Load(
        config.ResolvedPluginsDirectory,
        plugin => new PluginContext(plugin.Key, config, loggers, http, cacheRoot, services));

    // Now that every plugin's Configure has run, every indexer is known — build the real
    // grabber (config indexers + plugin indexers, one merged provider list) and bind it.
    // Pass the pre-built debrid so it's the same instance everywhere.
    var pluginIndexers = plugins.SelectMany(p => p.Indexers).ToList();

    // At most one provider tracker applies server-wide; reconcile across plugins (first in load
    // order wins, later ones logged and dropped). Own logger category — NOT SourceRouter.
    var providerTracker = ProviderTrackerReconciler.Resolve(
        plugins, loggers.CreateLogger(typeof(ProviderTrackerReconciler)));

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
    // Resolved from DI, not newed here: the singleton above is registered only when
    // config.Download.Enabled, so a disabled config yields null and the fallback stays off,
    // exactly as before — and a test host can replace the registration via
    // ConfigureTestServices. See the registration comment above for why the opt-in gate lives
    // there rather than in an inline `? : null` here.
    var downloader = sp.GetService<ITorrentDownloader>();

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
    // Capture the one music library a plugin registered (first in load order wins), for the
    // IMusicLibrary DI registration below. There is no config-driven equivalent — a music
    // library always comes from a plugin, same as metadata sources above. If more than one plugin
    // registered a non-null library, warn — naming the winner and the dropped key(s) — instead of
    // dropping the rest silently (mirrors ProviderTrackerReconciler.Resolve above).
    var withMusicLibrary = plugins.Where(p => p.MusicLibrary is not null).ToList();
    if (withMusicLibrary.Count > 1)
    {
        log.LogWarning(
            "{Count} plugins each registered a music library ({Keys}); only '{Winner}' (first in load order) is used.",
            withMusicLibrary.Count, string.Join(", ", withMusicLibrary.Select(p => p.Key)), withMusicLibrary[0].Key);
    }
    loadedMusicLibrary = withMusicLibrary.Count > 0 ? withMusicLibrary[0].MusicLibrary : null;

    loadedRomhackSources = plugins
        .SelectMany(p => p.RomhackSources ?? (IReadOnlyList<IRomhackSource>)Array.Empty<IRomhackSource>())
        .ToList();

    loadedHomebrewSources = plugins
        .SelectMany(p => p.HomebrewSources ?? (IReadOnlyList<IHomebrewSource>)Array.Empty<IHomebrewSource>())
        .ToList();

    var pluginMetadata = plugins.SelectMany(p => p.MetadataSources).ToList();
    var metadataSource = new MetadataBackedVideoSource(
        pluginMetadata, deferredGrabber, resolver, loggers.CreateLogger<MetadataBackedVideoSource>());

    var sources = plugins.SelectMany(p => p.Sources).Append(indexerSource).Append(metadataSource);
    return new SourceRouter(sources, log);
});

// The music library a plugin registered, exposed for the coming Subsonic-style API to resolve
// from DI. Resolving SourceRouter first forces plugin loading, so loadedMusicLibrary is set by
// the time we read it. When no plugin supplies one, the factory returns null and the API layer
// (Increment 3) skips mapping — GetService returns null, exactly as with the opt-in downloader.
builder.Services.AddSingleton<IMusicLibrary>(sp =>
{
    sp.GetRequiredService<SourceRouter>();
    return loadedMusicLibrary!;
});

// The romhack sources plugins registered, for the romhacks endpoints to fan out over. Resolving
// SourceRouter first forces plugin loading, exactly as above; with no plugin supplying one this is
// an empty list, and the endpoints answer "no hacks" rather than failing.
builder.Services.AddSingleton<IReadOnlyList<IRomhackSource>>(sp =>
{
    sp.GetRequiredService<SourceRouter>();
    return loadedRomhackSources;
});

// The homebrew sources plugins registered, for the homebrew endpoint to fan out over. Resolving
// SourceRouter first forces plugin loading, exactly as above; with no plugin supplying one this is
// an empty list, and the endpoint answers "none" rather than failing.
builder.Services.AddSingleton<IReadOnlyList<IHomebrewSource>>(sp =>
{
    sp.GetRequiredService<SourceRouter>();
    return loadedHomebrewSources;
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
    var query = ctx.Request.QueryString.Value ?? "";
    // ASP.NET's routing matches literal segments case-insensitively, so "/TOKEN/manifest.json"
    // reaches the same route as "/token/manifest.json" — the redaction has to match that or a
    // differently-cased request leaks the token into the log in plaintext (this was already
    // fixed once elsewhere in this file as a Critical; do not regress it here too).
    if (!string.IsNullOrEmpty(token)) path = path.Replace("/" + token, "/<token>", StringComparison.OrdinalIgnoreCase);

    // Subsonic /rest requests carry credentials in the QUERY, not the path: legacy p=<accessToken>
    // logs the whole-server token, and the t=md5(token+salt)&s=salt pair logs a replayable hash+salt.
    // The token-in-path redaction above never touches the query, so blank the entire /rest query string
    // before it reaches the log. StartsWithSegments matches "/rest" and "/rest/ping" but not "/restfoo".
    if (ctx.Request.Path.StartsWithSegments("/rest", StringComparison.OrdinalIgnoreCase) && query.Length > 0)
        query = "?<redacted>";

    log.LogInformation("{Method} {Path}{Query} -> {Status} ({Ms} ms)",
        ctx.Request.Method, path, query, ctx.Response.StatusCode, started.ElapsedMilliseconds);
});

app.MapGet("/", () => Results.Text(
    "EverythingBox Server.\n" +
    "Add this in the app via Add-ons -> Add addon by URL using:\n" +
    "    <this URL>/<token>/manifest.json   (the token, if configured)\n", "text/plain"));

app.MapGet("/health", () => Results.Json(new { ok = true }));

app.MapBrowse(prefix);
app.MapStreams(prefix);
app.MapFiles(prefix);
app.MapRomhacks(prefix);
app.MapHomebrew(prefix);

if (config.Sync.Enabled)
    app.MapSync(prefix);

// Subsonic mounts at a BARE /rest (no token prefix) — it authenticates per request against the
// access token. GetService (not GetRequiredService) forces the lazy plugin load and yields null
// when no plugin registered a music library, in which case the surface stays unmapped.
if (config.Subsonic.Enabled && app.Services.GetService<IMusicLibrary>() is not null)
    app.MapSubsonic();

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
