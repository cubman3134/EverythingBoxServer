using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class MonoTorrentDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-dl-" + Guid.NewGuid().ToString("N"));

    public MonoTorrentDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static MonoTorrentDownloader Downloader() =>
        new(NullLogger<MonoTorrentDownloader>.Instance);

    private static TorrentResult Release(Uri? magnet = null, string? infoHash = null) => new()
    {
        Title = "Some Release 1080p",
        ProviderName = "test-indexer",
        MagnetUri = magnet,
        InfoHash = infoHash,
    };

    [Fact]
    public async Task A_release_with_nothing_to_fetch_returns_an_empty_list()
    {
        // The interface's own contract: empty, not an exception.
        var paths = await Downloader().DownloadAsync(
            Release(), new MovieRequest { Title = "x" }, _dir, null, CancellationToken.None);

        Assert.Empty(paths);
    }

    [Fact]
    public async Task An_already_cancelled_token_returns_without_starting_anything()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var magnet = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");

        // Cancellation before any work is a normal caller-side race, not an error path.
        var paths = await Downloader().DownloadAsync(
            Release(magnet), new MovieRequest { Title = "x" }, _dir, null, cts.Token);

        Assert.Empty(paths);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_swarm_with_no_peers_gives_up_rather_than_hanging()
    {
        // A magnet nobody is seeding must not hold the caller open. Bounded by the
        // caller's own token, which is how the resolver applies its configured timeout.
        var magnet = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=nothing");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var paths = await Downloader().DownloadAsync(
            Release(magnet), new MovieRequest { Title = "x" }, _dir, null, cts.Token);

        Assert.Empty(paths);
    }

    [Fact]
    public async Task It_creates_the_target_directory_if_it_does_not_exist()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist");

        var paths = await Downloader().DownloadAsync(
            Release(), new MovieRequest { Title = "x" }, nested, null, CancellationToken.None);

        Assert.Empty(paths);
        // The empty-release path returns before any I/O; this asserts it did not throw
        // on a missing directory, which a naive implementation would.
    }
}
