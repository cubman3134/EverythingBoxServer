using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

file sealed class StubDebrid(DebridResult result) : IDebridService
{
    public string Name => "stub";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => Task.FromResult(result);
}

/// <summary>Records how many times it was asked to download, and writes fixed-content
/// placeholder files for whatever names it's told to "produce" — enough to exercise
/// ReleaseStreamResolver's fallback wiring without a real BitTorrent swarm.</summary>
file sealed class RecordingDownloader(params string[] produce) : ITorrentDownloader
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent, MediaRequest? request, string directory,
        IProgress<TorrentDownloadProgress>? progress = null, long? maxTotalBytes = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var name in produce)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, "x");
            paths.Add(path);
        }
        return Task.FromResult<IReadOnlyList<string>>(paths);
    }
}

/// <summary>Records requests it was asked to download and writes back a file whose
/// name reflects the episode requested — enough to tell whether two different
/// episode requests against the same release shared one memoized download.</summary>
file sealed class EpisodeAwareDownloader : ITorrentDownloader
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent, MediaRequest? request, string directory,
        IProgress<TorrentDownloadProgress>? progress = null, long? maxTotalBytes = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Directory.CreateDirectory(directory);
        var episode = (request as TvRequest)?.Episode ?? 0;
        var path = Path.Combine(directory, $"Some.Release.S01E{episode:00}.mkv");
        File.WriteAllText(path, "x");
        return Task.FromResult<IReadOnlyList<string>>([path]);
    }
}

/// <summary>Simulates a stalled swarm: reports the same byte count repeatedly (never
/// advancing) so the resolver's idle watchdog trips once the idle window elapses, then
/// honours the downloader contract by returning an empty list on cancellation. Uses real
/// wall-clock time, but there is no upper-bound race — it trips deterministically as soon
/// as the idle window passes, and keeps reporting until then.</summary>
file sealed class StalledSwarmDownloader : ITorrentDownloader
{
    public Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent, MediaRequest? request, string directory,
        IProgress<TorrentDownloadProgress>? progress = null, long? maxTotalBytes = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        return Task.Run<IReadOnlyList<string>>(async () =>
        {
            // First report primes the idle clock; every later report carries the SAME byte
            // count, so once the idle window elapses the watchdog cancels our linked token.
            while (!cancellationToken.IsCancellationRequested)
            {
                progress?.Report(new TorrentDownloadProgress(100, 1000, 0));
                try { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            // Contract: a cancelled download returns empty rather than throwing.
            return [];
        });
    }
}

/// <summary>Fails (returns nothing) on its first call, then succeeds on every call
/// after — enough to tell whether an empty result gets permanently memoized.</summary>
file sealed class FlakyDownloader : ITorrentDownloader
{
    private int _calls;
    public int Calls => _calls;

    public Task<IReadOnlyList<string>> DownloadAsync(
        TorrentResult torrent, MediaRequest? request, string directory,
        IProgress<TorrentDownloadProgress>? progress = null, long? maxTotalBytes = null, CancellationToken cancellationToken = default)
    {
        var n = Interlocked.Increment(ref _calls);
        Directory.CreateDirectory(directory);
        if (n == 1)
            return Task.FromResult<IReadOnlyList<string>>([]);

        var path = Path.Combine(directory, "Some.Release.1080p.mkv");
        File.WriteAllText(path, "x");
        return Task.FromResult<IReadOnlyList<string>>([path]);
    }
}

public class ReleaseStreamResolverTests : IDisposable
{
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private static TorrentResult Release() => new()
    {
        Title = "Some Release 1080p",
        ProviderName = "test-indexer",   // required by TorrentResult
        InfoHash = "0123456789abcdef0123456789abcdef01234567",
    };

    private static ReleaseStreamResolver Resolver(DebridResult? result) =>
        new(result is null ? null : new StubDebrid(result), NullLogger<ReleaseStreamResolver>.Instance);

    private static DebridResult Resolved(params DebridLink[] links)
        => DebridResult.Resolved("stub", "id", cached: true, links);

    [Fact]
    public async Task Without_a_debrid_service_there_is_nothing_to_play()
    {
        var stream = await Resolver(null).ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);
        Assert.Null(stream);
    }

    [Fact]
    public async Task A_resolved_release_becomes_a_playable_stream()
    {
        var resolver = Resolver(Resolved(new DebridLink("Movie.mkv", new Uri("https://example.test/a.mkv"), 100)));

        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/a.mkv", stream!.Url);
        Assert.Equal("video/x-matroska", stream.Mime);
    }

    [Fact]
    public async Task The_index_selects_a_later_link_so_a_user_can_reject_one()
    {
        var resolver = Resolver(Resolved(
            new DebridLink("a.mkv", new Uri("https://example.test/a.mkv"), 1),
            new DebridLink("b.mkv", new Uri("https://example.test/b.mkv"), 2)));

        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 1, CancellationToken.None);

        Assert.Equal("https://example.test/b.mkv", stream!.Url);
    }

    [Fact]
    public async Task An_index_past_the_end_yields_nothing_rather_than_throwing()
    {
        var resolver = Resolver(Resolved(new DebridLink("a.mkv", new Uri("https://example.test/a.mkv"), 1)));
        Assert.Null(await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 9, CancellationToken.None));
    }

    [Fact]
    public async Task A_pending_release_returns_a_notice_the_user_can_act_on()
    {
        var resolver = Resolver(DebridResult.Pending("stub", "id", "caching"));

        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("", stream!.Url);
        Assert.False(string.IsNullOrWhiteSpace(stream.Notice));
    }

    [Fact]
    public async Task A_failed_resolution_yields_nothing()
    {
        var resolver = Resolver(DebridResult.Failed("stub", "nope"));
        Assert.Null(await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None));
    }

    // --- ResolveAllAsync's Pending/Failed branches — MetadataBackedVideoSource is the
    // only caller (it walks a browse release's files before moving to the next
    // candidate), and nothing else in this file exercises them: every test above this
    // point calls ResolveAsync, not ResolveAllAsync. A reviewer mutating either branch
    // (Pending returning [] instead of a single notice option, or Failed returning a
    // bogus notice instead of []) left the suite fully green before these existed.

    [Fact]
    public async Task ResolveAllAsync_wraps_a_pending_release_as_a_single_notice_option()
    {
        var resolver = Resolver(DebridResult.Pending("stub", "id", "caching"));

        var options = await resolver.ResolveAllAsync(Release(), new MovieRequest { Title = "x" }, CancellationToken.None);

        var only = Assert.Single(options);
        Assert.Equal("", only.Url);
        Assert.False(string.IsNullOrWhiteSpace(only.Notice));
    }

    [Fact]
    public async Task ResolveAllAsync_yields_nothing_for_a_failed_resolution()
    {
        var resolver = Resolver(DebridResult.Failed("stub", "nope"));

        var options = await resolver.ResolveAllAsync(Release(), new MovieRequest { Title = "x" }, CancellationToken.None);

        Assert.Empty(options);
    }

    [Fact]
    public async Task A_debrid_service_that_throws_is_contained()
    {
        // Debrid is a network call to someone else's service. It failing must not 500.
        var resolver = new ReleaseStreamResolver(new ThrowingDebrid(), NullLogger<ReleaseStreamResolver>.Instance);
        Assert.Null(await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None));
    }

    [Fact]
    public async Task A_timeout_from_debrid_internal_is_contained_when_caller_did_not_cancel()
    {
        // HttpClient throws TaskCanceledException (which derives from OperationCanceledException) on its own internal
        // timeout. If the caller's token is NOT cancelled, this must be contained as "nothing playable", not escape as
        // an unhandled exception and 500 the request.
        var resolver = new ReleaseStreamResolver(new DebridThrowingTaskCanceled(), NullLogger<ReleaseStreamResolver>.Instance);
        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);
        Assert.Null(stream);
    }

    [Fact]
    public async Task A_genuine_caller_cancellation_propagates()
    {
        // A genuine caller cancellation (the token was actually cancelled by the caller) must still propagate,
        // not be swallowed into a null that hides a real client disconnect.
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var resolver = new ReleaseStreamResolver(
            new DebridRespectsToken(), NullLogger<ReleaseStreamResolver>.Instance);

        // When the token is already cancelled, ResolveAsync should throw OperationCanceledException,
        // not return null.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, cts.Token));
    }

    [Theory]
    [InlineData("a.mkv", "video/x-matroska")]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.epub", "application/epub+zip")]
    [InlineData("a.unknownext", "application/octet-stream")]
    public async Task Mime_follows_the_file_extension(string fileName, string expected)
    {
        var resolver = Resolver(Resolved(new DebridLink(fileName, new Uri("https://example.test/f"), 1)));
        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);
        Assert.Equal(expected, stream!.Mime);
    }

    // --- C1: the whole-torrent zip a debrid service prepends must never be index 0 ---

    private static DebridResult TorBoxShapedResult() => Resolved(
        new DebridLink("Some.Release.zip", new Uri("https://example.test/whole.zip"), 3_000_000),
        new DebridLink("Some.Release.S01E01.mkv", new Uri("https://example.test/e1.mkv"), 1_500_000),
        new DebridLink("Some.Release.S01E02.mkv", new Uri("https://example.test/e2.mkv"), 1_500_000));

    [Fact]
    public async Task A_torbox_shaped_multi_file_result_does_not_resolve_index_zero_to_the_zip()
    {
        // Shaped exactly like TorBoxService.RequestLinksAsync's output for a multi-file
        // torrent: the whole-torrent zip inserted at index 0, then the real files. A
        // season-pack request (no Episode) is one of the cases MediaFileMatcher itself
        // leaves untouched, so this is exactly the gap the resolver's own narrowing has
        // to cover.
        var resolver = Resolver(TorBoxShapedResult());
        var request = new TvRequest { Title = "Some Release" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.NotEqual("https://example.test/whole.zip", stream!.Url);
        Assert.Equal("https://example.test/e1.mkv", stream.Url);
    }

    [Fact]
    public async Task The_index_parameter_still_walks_real_candidates_not_the_archive()
    {
        // Reject-and-retry (?n=1, ?n=2, ...) must walk the narrowed, real files —
        // re-offering the same archive at every index would defeat the whole feature.
        var resolver = Resolver(TorBoxShapedResult());
        var request = new TvRequest { Title = "Some Release" };

        var stream = await resolver.ResolveAsync(Release(), request, 1, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/e2.mkv", stream!.Url);
    }

    [Fact]
    public async Task A_single_file_result_is_unaffected_by_narrowing()
    {
        var resolver = Resolver(Resolved(new DebridLink("Solo.Release.mkv", new Uri("https://example.test/solo.mkv"), 1_000)));
        var request = new TvRequest { Title = "Solo Release" }; // no Episode — would otherwise fall through unmatched

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.Equal("https://example.test/solo.mkv", stream!.Url);
    }

    [Fact]
    public async Task A_request_that_genuinely_wants_an_archive_still_gets_one()
    {
        // A ROM request can legitimately want the .zip itself (many emulator cores
        // consume zipped ROMs directly). MediaFileMatcher narrows to the requested
        // extension first; the resolver's archive-deprioritization must not then hide
        // the only files left just because they're all zips.
        var resolver = Resolver(Resolved(
            new DebridLink("Retro Pack (Disc 1).zip", new Uri("https://example.test/disc1.zip"), 200),
            new DebridLink("Retro Pack (Disc 2).zip", new Uri("https://example.test/disc2.zip"), 100)));
        var request = new GeneralRequest { Title = "Retro Pack", Kind = MediaType.PcGame, FileType = "zip" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.EndsWith(".zip", stream!.Url);
    }

    // --- B1: an id with no media type must not route through the general matcher ---

    [Fact]
    public async Task An_unknown_media_type_request_skips_the_matcher_and_still_gets_real_files()
    {
        // Before this fix, an UnknownMediaTypeRequest-shaped case was a plain
        // GeneralRequest{Kind=Other}: MatchGeneral would score the zip as the closest
        // "title" match to the release name (zero extra tokens, vs S01E01/S01E02 on the
        // real files) and narrow down to just the zip — leaving n=1 and n=2 null and
        // costing the user their only escape hatch (reject-and-retry). Skipping the
        // matcher for this case and letting the archive-deprioritization pass run alone
        // restores both real files, with the archive pushed after them.
        var resolver = Resolver(TorBoxShapedResult());
        var request = new UnknownMediaTypeRequest { Title = "Some Release" };

        var n0 = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);
        var n1 = await resolver.ResolveAsync(Release(), request, 1, CancellationToken.None);
        var n2 = await resolver.ResolveAsync(Release(), request, 2, CancellationToken.None);

        Assert.NotNull(n0);
        Assert.Equal("https://example.test/e1.mkv", n0!.Url);
        Assert.NotNull(n1);
        Assert.Equal("https://example.test/e2.mkv", n1!.Url);
        Assert.NotNull(n2);
        Assert.Equal("https://example.test/whole.zip", n2!.Url);
    }

    // --- B2: nothing else in the suite notices if MediaFileMatcher stops being called ---

    [Fact]
    public async Task The_matcher_narrows_a_comic_pack_to_the_requested_issue()
    {
        // Every other test in this file either has a single file, has files that are
        // already in the "right" order, or has no distinction the archive/sample pass
        // alone would get wrong — so deleting the MediaFileMatcher.SelectForRequest
        // call out of Narrow leaves every one of them green. This one doesn't: none of
        // the three links is an archive or a sample, so without the matcher the
        // deprioritization pass is a no-op and issue 1 (input order) wins index 0
        // instead of the requested issue 5.
        var resolver = Resolver(Resolved(
            new DebridLink("Big Comic 001.cbz", new Uri("https://example.test/1.cbz"), 100),
            new DebridLink("Big Comic 005.cbz", new Uri("https://example.test/5.cbz"), 100),
            new DebridLink("Big Comic 010.cbz", new Uri("https://example.test/10.cbz"), 100)));
        var request = new ComicRequest { Title = "Big Comic", Issue = 5 };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/5.cbz", stream!.Url);
    }

    // --- M1: an obvious sample file must not win index 0 ---

    [Fact]
    public async Task A_sample_file_no_longer_wins_index_zero()
    {
        var resolver = Resolver(Resolved(
            new DebridLink("Some.Release.zip", new Uri("https://example.test/whole.zip"), 3_000_000),
            new DebridLink("sample.mkv", new Uri("https://example.test/sample.mkv"), 50_000_000),
            new DebridLink("Some.Release.2020.1080p.mkv", new Uri("https://example.test/feature.mkv"), 1_500_000_000)));
        var request = new MovieRequest { Title = "Some Release" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/feature.mkv", stream!.Url);
    }

    [Theory]
    [InlineData("sample.mkv")]
    [InlineData("Some.Release-sample.mkv")]
    [InlineData("sample/Some.Release.mkv")]
    public async Task Conventionally_named_sample_files_are_pushed_after_the_feature(string sampleName)
    {
        var resolver = Resolver(Resolved(
            new DebridLink(sampleName, new Uri("https://example.test/sample"), 50_000_000),
            new DebridLink("Some.Release.2020.1080p.mkv", new Uri("https://example.test/feature.mkv"), 1_500_000_000)));
        var request = new MovieRequest { Title = "Some Release" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/feature.mkv", stream!.Url);
    }

    [Fact]
    public async Task A_release_merely_containing_the_word_sample_is_not_demoted_by_the_word_alone()
    {
        // The naming convention (an exact "sample" stem, a "-sample" suffix, or a
        // sample/ directory) is one signal, not a substring search — otherwise a
        // legitimately titled release would be wrongly deprioritized.
        var resolver = Resolver(Resolved(
            new DebridLink("Some.Release.zip", new Uri("https://example.test/whole.zip"), 3_000_000),
            new DebridLink("Sample.Movie.Title.2020.1080p.mkv", new Uri("https://example.test/real.mkv"), 1_500_000_000)));
        var request = new MovieRequest { Title = "Sample Movie Title" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/real.mkv", stream!.Url);
    }

    [Fact]
    public async Task A_sample_named_file_that_is_not_smaller_than_anything_else_is_not_demoted()
    {
        // The size signal matters too: nothing to prefer it over means nothing to lose
        // by leaving it where it was.
        var resolver = Resolver(Resolved(new DebridLink("sample.mkv", new Uri("https://example.test/only.mkv"), 1_000)));
        var request = new MovieRequest { Title = "x" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/only.mkv", stream!.Url);
    }

    // --- M2: the whole-archive extension list misses common multi-part/tar shapes ---

    [Theory]
    [InlineData("Some.Release.tar.gz")]
    [InlineData("Some.Release.tgz")]
    [InlineData("Some.Release.tar")]
    [InlineData("Some.Release.zipx")]
    [InlineData("Some.Release.r00")]
    [InlineData("Some.Release.part1.rar")]
    [InlineData("Some.Release.part01.rar")]
    public async Task Additional_whole_archive_shapes_are_pushed_after_the_real_file(string archiveName)
    {
        var resolver = Resolver(Resolved(
            new DebridLink(archiveName, new Uri("https://example.test/archive"), 3_000_000),
            new DebridLink("Some.Release.mkv", new Uri("https://example.test/real.mkv"), 1_500_000)));
        var request = new MovieRequest { Title = "Some Release" };

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/real.mkv", stream!.Url);
    }

    [Fact]
    public async Task An_iso_is_deliberately_kept_out_of_the_whole_archive_list()
    {
        // Unlike zip/rar/tar, an ISO can legitimately BE the deliverable — a disc
        // image for a game or retro request — so it must never be pushed behind a
        // true archive the way one of those would be.
        var resolver = Resolver(Resolved(
            new DebridLink("Some.Release.zip", new Uri("https://example.test/whole.zip"), 3_000_000),
            new DebridLink("Some.Release.iso", new Uri("https://example.test/disc.iso"), 700_000_000)));
        var request = new MovieRequest { Title = "Some Release" }; // matcher is a no-op here; only the reorder pass applies

        var stream = await resolver.ResolveAsync(Release(), request, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/disc.iso", stream!.Url);
    }

    // --- Task 3: falling back to a self-download when debrid says Pending ---

    private static DebridResult Pending() => DebridResult.Pending("stub", "id", "caching");

    private static TorrentResult Sized(int megabytes) => new()
    {
        Title = "Some Release 1080p",
        ProviderName = "test-indexer",
        InfoHash = "0123456789abcdef0123456789abcdef01234567",
        MagnetUri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
        SizeBytes = megabytes * 1024L * 1024L,
    };

    private static TorrentResult Unsized() => new()
    {
        Title = "Some Release 1080p",
        ProviderName = "test-indexer",
        InfoHash = "0123456789abcdef0123456789abcdef01234567",
        MagnetUri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
        // SizeBytes deliberately absent — the cap has nothing to check against.
    };

    private ReleaseStreamResolver ResolverWithDownload(
        DebridResult result, ITorrentDownloader downloader, DownloadConfig download)
    {
        var root = Path.Combine(Path.GetTempPath(), "ebs-fallback-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        return new ReleaseStreamResolver(
            new StubDebrid(result),
            NullLogger<ReleaseStreamResolver>.Instance,
            downloader,
            new FileCache(root),
            download);
    }

    [Fact]
    public async Task An_uncached_release_is_fetched_when_downloading_is_enabled()
    {
        var downloader = new RecordingDownloader("Some.Release.1080p.mkv");
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });

        var stream = await resolver.ResolveAsync(Sized(500), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.StartsWith("files/", stream!.Url);
        Assert.Equal(1, downloader.Calls);
    }

    [Fact]
    public async Task With_downloading_disabled_the_user_still_gets_the_caching_notice()
    {
        // The shipped default. Turning the feature off must change nothing else.
        var downloader = new RecordingDownloader("Some.Release.1080p.mkv");
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = false });

        var stream = await resolver.ResolveAsync(Sized(500), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.Equal("", stream!.Url);
        Assert.False(string.IsNullOrWhiteSpace(stream.Notice));
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task A_release_over_the_size_cap_is_not_fetched()
    {
        var downloader = new RecordingDownloader("huge.mkv");
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true, MaxSizeMB = 100 });

        var stream = await resolver.ResolveAsync(Sized(5000), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.Equal("", stream!.Url);
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task A_release_of_unknown_size_is_not_fetched()
    {
        // An unknown size is exactly what the cap exists to refuse.
        var downloader = new RecordingDownloader("mystery.mkv");
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });

        var stream = await resolver.ResolveAsync(Unsized(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task A_debrid_failure_does_not_trigger_a_download()
    {
        // Failed usually means a bad key or a rejected magnet. Starting a swarm is
        // the wrong answer to an authentication problem.
        var downloader = new RecordingDownloader("Some.Release.1080p.mkv");
        var resolver = ResolverWithDownload(DebridResult.Failed("stub", "nope"), downloader, new DownloadConfig { Enabled = true });

        Assert.Null(await resolver.ResolveAsync(Sized(500), new MovieRequest { Title = "x" }, 0, CancellationToken.None));
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public async Task A_download_that_produces_nothing_falls_back_to_the_notice()
    {
        var downloader = new RecordingDownloader();   // no files
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });

        var stream = await resolver.ResolveAsync(Sized(500), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.Equal("", stream!.Url);
        Assert.False(string.IsNullOrWhiteSpace(stream.Notice));
    }

    [Fact]
    public async Task Two_callers_wanting_the_same_release_download_it_once()
    {
        // FileCache already guarantees build-once; this asserts the fallback goes through it.
        var downloader = new RecordingDownloader("Some.Release.1080p.mkv");
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });
        var request = new MovieRequest { Title = "x" };

        var first = resolver.ResolveAsync(Sized(500), request, 0, CancellationToken.None);
        var second = resolver.ResolveAsync(Sized(500), request, 0, CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, downloader.Calls);
    }

    // --- C1: the memoized download must be keyed per-episode, not per-show ---

    [Fact]
    public async Task Two_viewers_wanting_different_episodes_of_the_same_release_get_different_files()
    {
        // Before the fix, ReleaseKey hashed only the show title for a TvRequest — Season
        // and Episode never entered it — so a season-pack release memoized ONE download
        // across every episode, and the second viewer silently got the first viewer's
        // episode back.
        var downloader = new EpisodeAwareDownloader();
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });

        var e1Task = resolver.ResolveAsync(
            Sized(500), new TvRequest { Title = "Some Release", Season = 1, Episode = 1 }, 0, CancellationToken.None);
        var e2Task = resolver.ResolveAsync(
            Sized(500), new TvRequest { Title = "Some Release", Season = 1, Episode = 2 }, 0, CancellationToken.None);
        var results = await Task.WhenAll(e1Task, e2Task);
        var (e1, e2) = (results[0], results[1]);

        Assert.Equal(2, downloader.Calls);
        Assert.NotNull(e1);
        Assert.NotNull(e2);
        Assert.NotEqual(e1!.Url, e2!.Url);
        Assert.Contains("S01E01", e1.Url);
        Assert.Contains("S01E02", e2.Url);
    }

    // --- C2: a transient empty download must not permanently disable the fallback ---

    [Fact]
    public async Task A_transient_empty_download_does_not_permanently_disable_the_fallback()
    {
        // Before the fix, _downloads never evicted an empty result — since
        // ReleaseStreamResolver is a process-lifetime singleton, one "no peers yet" miss
        // would disable the fallback for that release forever.
        var downloader = new FlakyDownloader();
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });
        var request = new MovieRequest { Title = "x" };

        var first = await resolver.ResolveAsync(Sized(500), request, 0, CancellationToken.None);
        var second = await resolver.ResolveAsync(Sized(500), request, 0, CancellationToken.None);

        Assert.Equal("", first!.Url);
        Assert.False(string.IsNullOrWhiteSpace(first.Notice));

        Assert.NotNull(second);
        Assert.StartsWith("files/", second!.Url);
        Assert.Equal(2, downloader.Calls);
    }

    // --- C3: a successful fallback must not keep two copies of the file on disk forever ---

    [Fact]
    public async Task A_successful_download_removes_the_downloads_working_copy_but_keeps_the_served_file()
    {
        var downloader = new RecordingDownloader("Some.Release.1080p.mkv");
        var root = Path.Combine(Path.GetTempPath(), "ebs-fallback-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        var files = new FileCache(root);
        var resolver = new ReleaseStreamResolver(
            new StubDebrid(Pending()), NullLogger<ReleaseStreamResolver>.Instance,
            downloader, files, new DownloadConfig { Enabled = true });

        var stream = await resolver.ResolveAsync(Sized(500), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.StartsWith("files/", stream!.Url);

        var servedPath = Path.Combine(root, stream.Url["files/".Length..]);
        Assert.True(File.Exists(servedPath));

        var downloadsRoot = Path.Combine(root, ".downloads");
        Assert.False(Directory.Exists(downloadsRoot) && Directory.EnumerateFileSystemEntries(downloadsRoot).Any());
    }

    // --- C4: the size gate must refuse a non-positive size, not just an unknown one ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_size_is_not_fetched(long sizeBytes)
    {
        var downloader = new RecordingDownloader("mystery.mkv");
        var resolver = ResolverWithDownload(Pending(), downloader, new DownloadConfig { Enabled = true });
        var release = new TorrentResult
        {
            Title = "Some Release 1080p",
            ProviderName = "test-indexer",
            InfoHash = "0123456789abcdef0123456789abcdef01234567",
            MagnetUri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
            SizeBytes = sizeBytes,
        };

        var stream = await resolver.ResolveAsync(release, new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.Equal("", stream!.Url);
        Assert.Equal(0, downloader.Calls);
    }

    // --- Idle detection: a stalled swarm is abandoned early, with the DISTINCT reason logged ---

    [Fact]
    public async Task A_stalled_swarm_is_abandoned_via_idle_detection_with_the_distinct_reason_logged()
    {
        // The downloader swallows the cancel and returns [] (its contract), so the resolver
        // must inspect its own idle token AFTER the empty result to tell the operator WHY the
        // fetch gave up — a stalled swarm rather than a generic empty result. A small idle
        // window keeps this fast and non-flaky: the watchdog trips as soon as ~1s elapses with
        // no byte advance, and the total TimeoutSeconds (default 600) stays well clear.
        var downloader = new StalledSwarmDownloader();
        var logger = new CapturingLogger<ReleaseStreamResolver>();
        var root = Path.Combine(Path.GetTempPath(), "ebs-fallback-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);
        var resolver = new ReleaseStreamResolver(
            new StubDebrid(Pending()), logger, downloader, new FileCache(root),
            new DownloadConfig { Enabled = true, IdleTimeoutSeconds = 1 });

        var stream = await resolver.ResolveAsync(Sized(500), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        // Idle detection cancels the stalled fetch; the request degrades to the caching notice.
        Assert.Equal("", stream!.Url);
        Assert.False(string.IsNullOrWhiteSpace(stream.Notice));
        // And the operator gets the distinct idle reason, not a generic "gave up".
        Assert.Contains(logger.Messages, m => m.Contains("received no new data"));
    }
}

/// <summary>Captures formatted log messages so a test can assert WHICH reason the resolver
/// logged — distinguishing an idle abandonment from a plain empty result.</summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}

file sealed class ThrowingDebrid : IDebridService
{
    public string Name => "throwing";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => throw new HttpRequestException("upstream is down");
}

file sealed class DebridThrowingTaskCanceled : IDebridService
{
    public string Name => "throwing-timeout";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => throw new TaskCanceledException("internal timeout from HttpClient");
}

file sealed class DebridRespectsToken : IDebridService
{
    public string Name => "respects-token";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DebridResult.Resolved(Name, "id", cached: true, []));
    }
}
