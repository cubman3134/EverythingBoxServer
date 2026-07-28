using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Boots the real host (the actual <c>Program.cs</c> entry point, via reflection on
/// its compiler-generated top-level-statements Main) against a temp directory holding
/// the "good" fixture, listening on a real loopback socket.
///
/// This does NOT use <c>WebApplicationFactory&lt;Program&gt;</c>'s default in-memory
/// TestServer. TestServer builds <c>HttpContext.Request.Path</c> by fully
/// URI-unescaping the target and never populates
/// <c>IHttpRequestFeature.RawTarget</c> at all (verified empirically against
/// Microsoft.AspNetCore.Mvc.Testing/TestHost 9.0.0: the feature is present but its
/// RawTarget is always ""). That destroys exactly the information
/// <c>AddonEndpoints.ParseSearch</c> needs to tell an encoded '&amp;' inside a search
/// term apart from a real parameter separator — every request looks identically
/// "already decoded" regardless of what was on the wire. Real Kestrel does not have
/// this problem: it preserves the raw request-target it received. So this factory
/// runs the real ASP.NET Core pipeline end-to-end over an actual socket instead.
/// </summary>
public sealed class PluginServerFactory : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-host-" + Guid.NewGuid().ToString("N"));
    private readonly int _port;
    private readonly HttpClient _client;

    public string PluginsDirectory => Path.Combine(_root, "plugins");
    public string FilesDirectory => Path.Combine(_root, "files");

    public PluginServerFactory()
    {
        var staged = Path.Combine(AppContext.BaseDirectory, "testplugins", "good");
        var dest = Path.Combine(PluginsDirectory, "good");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(staged))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));

        Directory.CreateDirectory(FilesDirectory);

        _port = GetFreeLoopbackPort();
        var configPath = Path.Combine(_root, "everythingbox-server.json");
        File.WriteAllText(configPath, $$"""{ "Listen": "http://127.0.0.1:{{_port}}" } """);

        // MUST be set here, not in some later hook: Program.cs calls ServerConfig.Load()
        // in its top-level statements, which run the instant Main is invoked below.
        Environment.SetEnvironmentVariable("EBS_PLUGINS_DIR", PluginsDirectory);
        Environment.SetEnvironmentVariable("EBS_FILES_DIR", FilesDirectory);
        Environment.SetEnvironmentVariable("EBS_CONFIG", configPath);

        StartRealHost();

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
        WaitUntilReady();
    }

    public HttpClient CreateClient() => _client;

    /// <summary>Invokes the compiler-generated top-level-statements entry point
    /// (named "&lt;Main&gt;$") on a background thread. It calls app.Run(), which blocks
    /// for the process lifetime — acceptable here since the test process exits
    /// once the run completes and the OS reclaims the socket.</summary>
    private static void StartRealHost()
    {
        var entryPoint = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("EverythingBox.Server.dll has no entry point.");

        var thread = new Thread(() =>
        {
            try
            {
                entryPoint.Invoke(null, [Array.Empty<string>()]);
            }
            catch (Exception ex)
            {
                // Swallow so a startup failure (e.g. port already taken) surfaces as a
                // WaitUntilReady timeout with a clear message, not a process crash —
                // an unhandled exception on a plain background thread is fatal to the
                // whole test run.
                Console.Error.WriteLine($"[PluginServerFactory] real host thread failed: {ex}");
            }
        })
        { IsBackground = true, Name = "ebs-real-host" };

        thread.Start();
    }

    private void WaitUntilReady()
    {
        using var ready = new HttpClient { BaseAddress = _client.BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using var response = ready.GetAsync("/health").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return;
            }
            catch
            {
                // Not listening yet.
            }
            Thread.Sleep(50);
        }
        throw new TimeoutException($"EverythingBox.Server did not become ready on http://127.0.0.1:{_port}/ in time.");
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _client.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
