using System.Net.Http.Headers;
using EverythingBox.Server;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;
using EverythingBox.Server.Routing;

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

builder.Services.AddSingleton(sp =>
{
    var host = sp.GetRequiredService<PluginHost>();
    var loggers = sp.GetRequiredService<ILoggerFactory>();
    var http = sp.GetRequiredService<HttpClient>();
    var cacheRoot = Path.Combine(config.ResolvedFilesCacheDir, "plugins");

    var plugins = host.Load(
        config.ResolvedPluginsDirectory,
        plugin => new PluginContext(plugin.Key, config, loggers, http, cacheRoot));

    return new SourceRouter(plugins.SelectMany(p => p.Sources));
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
    if (!string.IsNullOrEmpty(token)) path = path.Replace("/" + token, "/<token>", StringComparison.Ordinal);

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

// Force plugin loading now, so failures surface at startup rather than on first request.
_ = app.Services.GetRequiredService<SourceRouter>();

log.LogInformation("Listening on {Urls}", config.Listen);
app.Run();

// Exposed so WebApplicationFactory-style tests can reference it.
public partial class Program;
