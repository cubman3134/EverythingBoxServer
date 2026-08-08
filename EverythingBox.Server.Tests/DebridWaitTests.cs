using System.Net;
using System.Text;
using System.Text.Json;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// <c>Debrid.WaitSeconds</c> (task 3 of the 3d2 config migration): threads a configured/
/// per-user wait through <see cref="GrabberFactory.CreateDebrid(string?, string?, HttpClient, ILogger)"/>
/// into <c>TorBoxOptions.MaxWait</c>/<c>RealDebridOptions.MaxWait</c> — the poll-until-ready loop
/// the debrid services already implement. <c>MaxWait</c> is an <c>init</c>-only property with no
/// public getter surface on the built service, so these tests prove it was actually threaded
/// behaviourally: a release that is NOT ready on the first poll but IS ready on the second only
/// resolves (rather than reporting <see cref="DebridStatus.Pending"/> immediately) when the wait
/// handed in is greater than zero.
/// </summary>
public class DebridWaitTests
{
    private static TorrentResult Torrent() => new()
    {
        Title = "The Matrix 1999 1080p BluRay",
        ProviderName = "test",
        MagnetUri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
        InfoHash = "0123456789abcdef0123456789abcdef01234567",
    };

    [Fact]
    public async Task CreateDebrid_threads_maxWait_into_the_TorBox_services_MaxWait()
    {
        var handler = new PollTwiceThenReadyHandler();
        var http = new HttpClient(handler);
        var log = NullLoggerFactory.Instance.CreateLogger("test");

        var debrid = GrabberFactory.CreateDebrid("torbox", "K", http, log, maxWait: TimeSpan.FromSeconds(30));
        Assert.NotNull(debrid);

        var result = await debrid!.ResolveAsync(Torrent());

        // A MaxWait that was still Zero would return Pending after the FIRST not-ready poll,
        // never reaching the handler's second (ready) answer.
        Assert.Equal(DebridStatus.Resolved, result.Status);
        Assert.Equal(2, handler.MylistCalls);
    }

    [Fact]
    public async Task BuildDebrid_threads_configured_WaitSeconds_into_MaxWait()
    {
        var handler = new PollTwiceThenReadyHandler();
        var config = new ServerConfig { Debrid = new DebridConfig { Provider = "torbox", ApiKey = "K", WaitSeconds = 30 } };

        var debrid = GrabberFactory.BuildDebrid(config, new HttpClient(), NullLoggerFactory.Instance, transport: handler);
        Assert.NotNull(debrid);

        var result = await debrid!.ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Resolved, result.Status);
        Assert.Equal(2, handler.MylistCalls);
    }

    [Fact]
    public async Task ServerServices_CreateDebrid_uses_the_debridWait_it_was_constructed_with()
    {
        var handler = new PollTwiceThenReadyHandler();
        IServerServices services = new ServerServices(
            grabber: null!,
            debrid: null,
            files: null!,
            http: new HttpClient(),
            loggerFactory: NullLoggerFactory.Instance,
            transport: handler,
            debridWait: TimeSpan.FromSeconds(30));

        var debrid = services.CreateDebrid("torbox", "K");
        Assert.NotNull(debrid);

        var result = await debrid!.ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Resolved, result.Status);
        Assert.Equal(2, handler.MylistCalls);
    }

    [Fact]
    public async Task An_omitted_WaitSeconds_still_builds_a_cached_only_MaxWait_zero_service()
    {
        // The neutral-default case: WaitSeconds defaults to 0, so a not-ready first poll must
        // still return Pending immediately, exactly like before WaitSeconds existed — even
        // though the handler WOULD report ready on a second poll it never gets to make.
        var handler = new PollTwiceThenReadyHandler();
        var config = new ServerConfig { Debrid = new DebridConfig { Provider = "torbox", ApiKey = "K" } };

        var debrid = GrabberFactory.BuildDebrid(config, new HttpClient(), NullLoggerFactory.Instance, transport: handler);
        Assert.NotNull(debrid);

        var result = await debrid!.ResolveAsync(Torrent());

        Assert.Equal(DebridStatus.Pending, result.Status);
        Assert.Equal(1, handler.MylistCalls);
    }
}

/// <summary>
/// Stands in for a TorBox account holding one release that is not yet ready on its first
/// <c>mylist</c> poll but IS ready from the second poll onward. Whether a caller ever SEES the
/// second answer is exactly what proves a positive <c>MaxWait</c> reached the service: with
/// <c>MaxWait = TimeSpan.Zero</c> (the pre-task-3 default), <c>TorBoxService</c> gives up after
/// the first not-ready poll and never asks again.
/// </summary>
file sealed class PollTwiceThenReadyHandler : HttpMessageHandler
{
    private int _mylistCalls;

    public int MylistCalls => Volatile.Read(ref _mylistCalls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;

        if (path.EndsWith("createtorrent", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Json(new { success = true, data = new { torrent_id = 1 } }));

        if (path.EndsWith("mylist", StringComparison.OrdinalIgnoreCase))
        {
            var call = Interlocked.Increment(ref _mylistCalls);
            var ready = call >= 2;
            return Task.FromResult(Json(new
            {
                success = true,
                data = new
                {
                    download_finished = ready,
                    download_present = ready,
                    files = ready
                        ? new object[] { new { id = 1, name = "release.mkv", size = 123L } }
                        : Array.Empty<object>(),
                },
            }));
        }

        if (path.EndsWith("requestdl", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Json(new { success = true, data = "https://example.test/download/release.mkv" }));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
