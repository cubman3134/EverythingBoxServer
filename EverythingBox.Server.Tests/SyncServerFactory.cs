using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Tests;

/// <summary>Captures every formatted log message, so a test can assert on what actually got
/// written without depending on any real logging provider (console, file, ...).</summary>
file sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
    public void Dispose() { }

    private sealed class CapturingLogger(List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink) sink.Add(formatter(state, exception));
        }
    }
}

/// <summary>
/// Mirrors <see cref="TokenPluginServerFactory"/> — same token-prefixed host with a captured
/// log — but writes a <c>Sync</c>-enabled config so the /{token}/sync routes are mapped. The
/// quota is deliberately tiny (65536 per namespace, 32768 per object) so the quota and
/// too-large tests exercise the caps with small bodies. Its own non-parallel collection keeps
/// its EBS_* environment writes from racing the other server factories.
/// </summary>
public sealed class SyncServerFactory : WebApplicationFactory<Program>
{
    public const string Token = "sync-tok";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-sync-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");
    public string SyncDirectory => Path.Combine(_root, "sync");

    public List<string> LoggedMessages { get; } = [];

    public SyncServerFactory()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", "good");
        var dest = Path.Combine(PluginsDirectory, "good");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(SyncDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath,
            "{ \"AccessToken\": \"" + Token + "\", " +
            "\"Sync\": { \"Enabled\": true, \"Directory\": " + System.Text.Json.JsonSerializer.Serialize(SyncDirectory) + ", " +
            "\"PerNamespaceQuotaBytes\": 65536, \"MaxObjectBytes\": 32768 } }");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_SYNC_DIR", SyncDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureLogging(logging => logging.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(LoggedMessages)));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SyncServerCollection : ICollectionFixture<SyncServerFactory>
{
    public const string Name = "sync-server";
}
