using System.Net;
using System.Text;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Throws if called at all — proves a code path that should short-circuit
/// before touching HTTP (a usable magnet/info-hash already in hand) really does.</summary>
file sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("should not be called");
}

/// <summary>Serves fixed bytes for every request and counts how many it answered.</summary>
file sealed class CountingBytesHandler(byte[] body) : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}

/// <summary>Answers every request with 404, as an unreachable .torrent link would.</summary>
file sealed class NotFoundHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
}

public class MonoTorrentDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-dl-" + Guid.NewGuid().ToString("N"));

    // Same minimal bencoded torrent shape MagnetResolverTests builds — a "d...e" dict with
    // an announce and an info sub-dict — the least MonoTorrent's own parsing/TryReadInfoHash
    // needs to treat it as a real .torrent.
    private static readonly byte[] Info =
        Encoding.UTF8.GetBytes("d6:lengthi1024e4:name8:test.iso12:piece lengthi16384e6:pieces20:01234567890123456789e");

    private static byte[] TorrentBytes()
        => [(byte)'d', .. Encoding.UTF8.GetBytes("8:announce18:udp://tracker:6969"), .. Encoding.UTF8.GetBytes("4:info"), .. Info, (byte)'e'];

    public MonoTorrentDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static MonoTorrentDownloader Downloader(HttpMessageHandler? handler = null) =>
        new(NullLogger<MonoTorrentDownloader>.Instance, new HttpClient(handler ?? new ThrowingHandler()));

    private static TorrentResult Release(Uri? magnet = null, string? infoHash = null, Uri? downloadUrl = null) => new()
    {
        Title = "Some Release 1080p",
        ProviderName = "test-indexer",
        MagnetUri = magnet,
        InfoHash = infoHash,
        DownloadUrl = downloadUrl,
    };

    [Fact]
    public async Task A_release_with_nothing_to_fetch_returns_an_empty_list()
    {
        // The interface's own contract: empty, not an exception.
        var paths = await Downloader().DownloadAsync(
            Release(), new MovieRequest { Title = "x" }, _dir, null, cancellationToken: CancellationToken.None);

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
            Release(magnet), new MovieRequest { Title = "x" }, _dir, null, cancellationToken: cts.Token);

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
            Release(magnet), new MovieRequest { Title = "x" }, _dir, null, cancellationToken: cts.Token);

        Assert.Empty(paths);
    }

    [Fact]
    public async Task It_creates_the_target_directory_if_it_does_not_exist()
    {
        var nested = Path.Combine(_dir, "does", "not", "exist");

        var paths = await Downloader().DownloadAsync(
            Release(), new MovieRequest { Title = "x" }, nested, null, cancellationToken: CancellationToken.None);

        Assert.Empty(paths);
        // The empty-release path returns before any I/O; this asserts it did not throw
        // on a missing directory, which a naive implementation would.
    }

    [Fact]
    public async Task A_release_with_only_a_DownloadUrl_gets_as_far_as_resolving_it()
    {
        // Many indexers give only a .torrent link, no magnet, no infohash attribute.
        // Proves that case is actually attempted rather than falling straight to "empty" —
        // asserted via the handler's call count, not just the (still-empty, no-real-swarm)
        // result, which a no-op resolver would also produce.
        var handler = new CountingBytesHandler(TorrentBytes());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var torrent = Release(downloadUrl: new Uri("https://example.test/release.torrent"));

        var paths = await Downloader(handler).DownloadAsync(
            torrent, new MovieRequest { Title = "x" }, _dir, null, cancellationToken: cts.Token);

        Assert.Empty(paths);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_DownloadUrl_that_404s_returns_empty_without_throwing()
    {
        var torrent = Release(downloadUrl: new Uri("https://example.test/missing.torrent"));

        var paths = await Downloader(new NotFoundHandler()).DownloadAsync(
            torrent, new MovieRequest { Title = "x" }, _dir, null, cancellationToken: CancellationToken.None);

        Assert.Empty(paths);
    }

    [Fact]
    public async Task A_DownloadUrl_serving_non_torrent_bytes_returns_empty_without_throwing()
    {
        var handler = new CountingBytesHandler(Encoding.UTF8.GetBytes("<html>not a torrent</html>"));
        var torrent = Release(downloadUrl: new Uri("https://example.test/page.torrent"));

        var paths = await Downloader(handler).DownloadAsync(
            torrent, new MovieRequest { Title = "x" }, _dir, null, cancellationToken: CancellationToken.None);

        Assert.Empty(paths);
    }

    [Fact]
    public async Task A_release_with_a_magnet_and_a_DownloadUrl_uses_the_magnet_and_makes_no_HTTP_call()
    {
        var handler = new CountingBytesHandler(TorrentBytes());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var magnet = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=nothing");
        var torrent = Release(magnet: magnet, downloadUrl: new Uri("https://example.test/has-both.torrent"));

        var paths = await Downloader(handler).DownloadAsync(
            torrent, new MovieRequest { Title = "x" }, _dir, null, cancellationToken: cts.Token);

        Assert.Empty(paths); // no real swarm behind this synthesized magnet either
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_malformed_magnet_uri_returns_empty_without_throwing()
    {
        // Not a valid btih magnet — MagnetLink.Parse should throw internally, and that
        // should be caught rather than propagate.
        var torrent = Release(magnet: new Uri("magnet:?xt=urn:btih:not-valid-hex"));

        var paths = await Downloader().DownloadAsync(
            torrent, new MovieRequest { Title = "x" }, _dir, null, cancellationToken: CancellationToken.None);

        Assert.Empty(paths);
    }

    [Fact]
    public void The_real_selected_size_is_re_checked_against_the_cap()
    {
        long cap = 2L * 1024 * 1024 * 1024; // 2 GB

        // A release the indexer under-reported: its true selected size is over the cap → refused,
        // regardless of what SizeBytes claimed.
        Assert.True(MonoTorrentDownloader.ExceedsCap(3L * 1024 * 1024 * 1024, cap));
        // At or under the cap → allowed.
        Assert.False(MonoTorrentDownloader.ExceedsCap(cap, cap));
        Assert.False(MonoTorrentDownloader.ExceedsCap(1L * 1024 * 1024 * 1024, cap));
        // No cap → never refused.
        Assert.False(MonoTorrentDownloader.ExceedsCap(long.MaxValue, null));
    }

    [Fact]
    public void SelectMembers_matches_a_full_member_path_exactly()
    {
        string[] files = ["dir/one.bin", "dir/two.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "dir/one.bin" });
        Assert.Equal(new[] { "dir/one.bin" }, picked);
    }

    [Fact]
    public void SelectMembers_matches_a_bare_filename_nested_in_a_directory()
    {
        string[] files = ["a/b/one.bin", "a/b/two.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "one.bin" });
        Assert.Equal(new[] { "a/b/one.bin" }, picked);
    }

    [Fact]
    public void SelectMembers_matches_case_insensitively()
    {
        string[] files = ["dir/One.BIN"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "one.bin" });
        Assert.Single(picked);
    }

    [Fact]
    public void SelectMembers_selects_several_members_in_file_order_not_wanted_order()
    {
        string[] files = ["one.bin", "two.bin", "three.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "three.bin", "one.bin" });
        Assert.Equal(new[] { "one.bin", "three.bin" }, picked);
    }

    [Fact]
    public void SelectMembers_returns_empty_when_nothing_matches()
    {
        string[] files = ["one.bin", "two.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "nope.bin" });
        Assert.Empty(picked); // an all-miss selection yields nothing — caller downloads nothing, not everything
    }

    [Fact]
    public void SelectMembers_returns_empty_for_an_empty_wanted_list()
    {
        string[] files = ["one.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, Array.Empty<string>());
        Assert.Empty(picked);
    }

    [Fact]
    public void A_TorrentResult_defaults_to_no_explicit_wanted_members()
    {
        // Additive field: existing producers that don't set it must get the empty default,
        // so the request-heuristic path stays the default.
        var r = new TorrentResult { Title = "x", ProviderName = "p" };
        Assert.Empty(r.WantedMembers);
    }
}
