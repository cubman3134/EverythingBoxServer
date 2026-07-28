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
/// F4: boots the real host with an AccessToken configured (<see cref="PluginServerFactory"/>
/// always writes an empty "{}" config, so its whole suite runs with an empty token prefix —
/// the server's only authentication mechanism was entirely unexercised). Also captures every
/// log line so a test can assert the token never appears in it, regardless of the casing the
/// request used.
///
/// This is its own <see cref="TokenServerCollection"/> rather than joining
/// <see cref="AddonServerCollection"/>, per that collection's own doc comment: it does not
/// share <see cref="PluginServerFactory"/>'s instance, so folding it in would not remove any
/// race — instead this gets its own collection, also marked non-parallel. The assembly-level
/// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c> in
/// <c>AssemblyInfo.cs</c> serialises collections against EACH OTHER too, so this factory's
/// EBS_* environment-variable writes still never race <see cref="PluginServerFactory"/>'s.
/// </summary>
public sealed class TokenPluginServerFactory : WebApplicationFactory<Program>
{
    public const string Token = "s3cret-tok";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-token-host-" + Guid.NewGuid().ToString("N"));

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");

    public List<string> LoggedMessages { get; } = [];

    public TokenPluginServerFactory()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", "good");
        var dest = Path.Combine(PluginsDirectory, "good");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        Directory.CreateDirectory(FilesDirectory);

        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, "{ \"AccessToken\": \"" + Token + "\" }");

        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
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
public sealed class TokenServerCollection : ICollectionFixture<TokenPluginServerFactory>
{
    public const string Name = "token-server";
}
