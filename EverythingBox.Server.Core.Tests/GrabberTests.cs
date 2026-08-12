using EverythingBox.Server.Abstractions;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class GrabberTests
{
    private static TorrentResult Res(string title, string hash, int seeders, string provider = "p")
        => new()
        {
            Title = title,
            ProviderName = provider,
            InfoHash = hash,
            MagnetUri = new Uri($"magnet:?xt=urn:btih:{hash}"),
            Seeders = seeders,
        };

    private static MovieRequest Matrix => new() { Title = "The Matrix", Year = 1999 };

    // --- Dedupe ------------------------------------------------------------

    [Fact]
    public async Task DedupeByInfoHashKeepsMostSeeded()
    {
        var low = Res("The Matrix 1999 1080p BluRay", "ABC", 10, "p1");
        var high = Res("The Matrix 1999 1080p BluRay", "abc", 200, "p2"); // same hash, different case

        var grabber = new TorrentGrabber([new ListProvider("p1", [low]), new ListProvider("p2", [high])]);
        var result = await grabber.GrabAsync(Matrix);

        Assert.Single(result.Ranked);
        Assert.Equal(200, result.Best!.Seeders);
        Assert.Equal("p2", result.Best.ProviderName);
    }

    [Fact]
    public async Task DedupeUnifiesMagnetHashWithStatedInfoHash()
    {
        var stated = Res("The Matrix 1999 1080p BluRay", "DEAD", 5, "p1");
        var viaMagnet = new TorrentResult
        {
            Title = "The Matrix 1999 1080p BluRay",
            ProviderName = "p2",
            MagnetUri = new Uri("magnet:?xt=urn:btih:dead&dn=the.matrix"),
            Seeders = 99,
        };

        var grabber = new TorrentGrabber([new ListProvider("p1", [stated]), new ListProvider("p2", [viaMagnet])]);
        var result = await grabber.GrabAsync(Matrix);

        Assert.Single(result.Ranked);
        Assert.Equal(99, result.Best!.Seeders);
    }

    [Fact]
    public async Task DistinctReleasesAreKept()
    {
        var a = Res("The Matrix 1999 1080p BluRay", "AAA", 10);
        var b = Res("The Matrix 1999 720p WEB-DL", "BBB", 10);

        var grabber = new TorrentGrabber([new ListProvider("p", [a, b])]);
        var result = await grabber.GrabAsync(Matrix);

        Assert.Equal(2, result.Ranked.Count);
    }

    // --- Parallel aggregation & error capture ------------------------------

    [Fact]
    public async Task AggregatesAcrossProvidersAndCapturesErrors()
    {
        var good = new ListProvider("good", [Res("The Matrix 1999 1080p BluRay", "H1", 10)]);
        var bad = new ThrowingProvider("bad");

        var grabber = new TorrentGrabber([good, bad]);
        var result = await grabber.GrabAsync(Matrix);

        Assert.True(result.Found);
        Assert.Single(result.Errors);
        Assert.Equal("bad", result.Errors[0].ProviderName);
    }

    [Fact]
    public async Task SlowProviderTimesOutButFastOneSucceeds()
    {
        var slow = new ListProvider("slow", [Res("The Matrix 1999 2160p BluRay", "SLOW", 999)], delayMs: 10_000);
        var fast = new ListProvider("fast", [Res("The Matrix 1999 1080p BluRay", "FAST", 10)]);

        var grabber = new TorrentGrabber(
            [slow, fast],
            options: new GrabberOptions { ProviderTimeout = TimeSpan.FromMilliseconds(150) });
        var result = await grabber.GrabAsync(Matrix);

        Assert.True(result.Found);
        Assert.Equal("FAST", result.Best!.InfoHash);
        Assert.Contains(result.Errors, e => e.ProviderName == "slow");
    }

    // C2: provider.Name is plugin-authored and can throw. QueryProviderAsync used to read
    // it fresh inside its own catch handlers, so a throwing Name escaped unguarded and
    // propagated out of SearchAsync — taking down every other provider's results in the
    // same batch, not just the bad one's. A name must be captured once, defensively, and
    // reused for every exit path including the catch.
    [Fact]
    public async Task A_provider_with_a_throwing_Name_does_not_take_down_the_whole_search()
    {
        var good = new ListProvider("good", [Res("The Matrix 1999 1080p BluRay", "H1", 10)]);
        var badName = new NameThrowingProvider();

        var grabber = new TorrentGrabber([good, badName]);

        var results = await grabber.SearchAsync(Matrix);

        Assert.Single(results);
        Assert.Equal("H1", results[0].InfoHash);
    }

    [Fact]
    public async Task A_provider_with_a_throwing_Name_is_reported_under_a_safe_placeholder_not_crashing_GrabAsync()
    {
        var good = new ListProvider("good", [Res("The Matrix 1999 1080p BluRay", "H1", 10)]);
        var badName = new NameThrowingProvider();

        var grabber = new TorrentGrabber([good, badName]);
        var result = await grabber.GrabAsync(Matrix);

        Assert.True(result.Found);
        Assert.Equal("H1", result.Best!.InfoHash);
        Assert.Single(result.Errors);
        Assert.NotNull(result.Errors[0].ProviderName);
        Assert.NotEqual(string.Empty, result.Errors[0].ProviderName);
    }

    // --- Provider-supplied ParsedInfo preservation -------------------------

    // A provider can attach its own ParsedInfo (carrying a ReleaseGroup) so the ranker
    // can score it. Prepare only parses a title when ParsedInfo is null, so a
    // provider-supplied ParsedInfo must survive untouched through the ranked search.
    // This is currently incidental; pin it so a future refactor cannot silently drop it.
    [Fact]
    public async Task Prepare_preserves_a_provider_supplied_ParsedInfo()
    {
        var preParsed = new TorrentResult
        {
            Title = "The Matrix 1999 1080p BluRay",
            ProviderName = "p",
            InfoHash = "PRE",
            MagnetUri = new Uri("magnet:?xt=urn:btih:PRE"),
            Seeders = 10,
            ParsedInfo = new ReleaseInfo { ReleaseGroup = "GroupA" },
        };

        var grabber = new TorrentGrabber([new ListProvider("p", [preParsed])]);
        var result = await grabber.GrabAsync(Matrix);

        Assert.Equal("GroupA", result.Best!.ParsedInfo!.ReleaseGroup);
    }

    // --- Quick grab --------------------------------------------------------

    [Fact]
    public async Task QuickGrabStopsAtThresholdAndIgnoresSlowerProviders()
    {
        // A fast provider returns a good-enough release; a slow provider would
        // return an even better one but should be cancelled before it lands.
        var fast = new ListProvider("fast", [Res("The Matrix 1999 1080p BluRay x264", "FAST", 100)]);
        var slow = new ListProvider("slow", [Res("The Matrix 1999 2160p BluRay REMUX", "SLOW", 50)], delayMs: 10_000);

        var grabber = new TorrentGrabber(
            [fast, slow],
            options: new GrabberOptions { QuickGrabScore = 50 });

        var result = await grabber.GrabAsync(Matrix);

        Assert.True(result.Found);
        Assert.Equal("FAST", result.Best!.InfoHash);                 // the quick pick
        Assert.DoesNotContain(result.Ranked, s => s.Result.InfoHash == "SLOW");
    }

    [Fact]
    public async Task QuickGrabFallsBackToFullRankingWhenNothingClearsBar()
    {
        var a = new ListProvider("a", [Res("The Matrix 1999 1080p BluRay x264", "A", 100)]);
        var b = new ListProvider("b", [Res("The Matrix 1999 2160p BluRay REMUX", "B", 50)]);

        // Nothing can reach this score, so it must wait for and rank everything.
        var grabber = new TorrentGrabber([a, b], options: new GrabberOptions { QuickGrabScore = 10_000 });

        var result = await grabber.GrabAsync(Matrix);

        Assert.True(result.Found);
        Assert.Equal(2, result.Ranked.Count);
    }

    // --- Cached-aware grab -------------------------------------------------

    [Fact]
    public async Task PrefersCachedReleaseEvenWhenLowerScored()
    {
        var cachedLowQuality = Res("The Matrix 1999 720p WEBRip x264", "lowc", 10);
        var uncachedHighQuality = Res("The Matrix 1999 2160p BluRay REMUX", "highn", 10);

        var grabber = new TorrentGrabber(
            [new ListProvider("p", [uncachedHighQuality, cachedLowQuality])],
            options: new GrabberOptions { PreferCachedReleases = true },
            debridService: new CachedDebrid("lowc"));

        var result = await grabber.GrabAsync(Matrix);

        Assert.Equal("lowc", result.Best!.InfoHash); // cached floats above the higher-scored release
        Assert.True(result.Ranked[0].Cached);
    }

    [Fact]
    public async Task QuickGrabHoldsOutForACachedHit()
    {
        // A non-cached high-quality release arrives first; a cached one arrives a
        // bit later. With PreferCachedReleases, the quick grab must wait for cached.
        var fastUncached = new ListProvider("fast", [Res("The Matrix 1999 2160p BluRay REMUX", "fastn", 100)]);
        var slowCached = new ListProvider("slow", [Res("The Matrix 1999 1080p BluRay x264", "slowc", 50)], delayMs: 150);

        var grabber = new TorrentGrabber(
            [fastUncached, slowCached],
            options: new GrabberOptions { QuickGrabScore = 50, PreferCachedReleases = true },
            debridService: new CachedDebrid("slowc"));

        var result = await grabber.GrabAsync(Matrix);

        Assert.Equal("slowc", result.Best!.InfoHash);
        Assert.True(result.Ranked.First(s => s.Result.InfoHash == "slowc").Cached);
    }

    // --- Provider performance tracking -------------------------------------

    [Fact]
    public async Task RecordsOutcomesAndMarksTheBestProvider()
    {
        var weak = new ListProvider("weak", [Res("The Matrix 1999 720p WEBRip x264", "W", 10, "weak")]);
        var strong = new ListProvider("strong", [Res("The Matrix 1999 1080p BluRay x264", "S", 10, "strong")]);
        var tracker = new RecordingTracker();

        var grabber = new TorrentGrabber([weak, strong], providerTracker: tracker);
        var result = await grabber.GrabAsync(Matrix);

        Assert.True(tracker.PrioritizeCalled);
        var outcomes = Assert.Single(tracker.Recorded);
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(1, o.ResultCount));

        var winner = Assert.Single(outcomes, o => o.ProducedBest);
        Assert.Equal("strong", winner.ProviderName); // the 1080p BluRay
    }

    private sealed class RecordingTracker : IProviderPerformanceTracker
    {
        public bool PrioritizeCalled { get; private set; }
        public List<IReadOnlyList<ProviderOutcome>> Recorded { get; } = [];

        public IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> providers)
        {
            PrioritizeCalled = true;
            return providers;
        }

        public void Record(IReadOnlyList<ProviderOutcome> outcomes) => Recorded.Add(outcomes);
    }

    // --- Multi-title fan-out (EBS#2 Task 3) --------------------------------

    // Regression: with no alternate titles the fan-out is exactly one query per capable
    // provider — same count and results as before the title × provider expansion.
    [Fact]
    public async Task NoAlternateTitles_IsStillOneQueryPerProvider()
    {
        var a = new CountingProvider("a", [Res("The Matrix 1999 1080p BluRay", "A", 10, "a")]);
        var b = new CountingProvider("b", [Res("The Matrix 1999 720p WEB-DL", "B", 10, "b")]);
        var c = new CountingProvider("c", [Res("The Matrix 1999 2160p BluRay", "C", 10, "c")]);

        var grabber = new TorrentGrabber([a, b, c]);
        var result = await grabber.SearchRankedAsync(Matrix); // Matrix has empty AlternateTitles

        Assert.Equal(1, a.Calls);
        Assert.Equal(1, b.Calls);
        Assert.Equal(1, c.Calls);
        Assert.Equal(3, result.Count); // three distinct releases, one per provider
    }

    // Merge: both the primary and the alternate title are queried, and a hit that only
    // matches the alternate is accepted and merged into the ranked list.
    [Fact]
    public async Task AlternateTitle_ProducesADistinctHitThatIsMerged()
    {
        var request = new MovieRequest { Title = "Spirited Away", AlternateTitles = ["Sen to Chihiro"] };
        var grabber = new TorrentGrabber([new QueryTermProvider("p")]);

        var result = await grabber.SearchRankedAsync(request);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.InfoHash == HashFor("Spirited Away"));
        Assert.Contains(result, r => r.InfoHash == HashFor("Sen to Chihiro"));
    }

    // Dedup across titles: a provider that returns the SAME release for every query term
    // collapses to a single result after the cross-title merge.
    [Fact]
    public async Task SameReleaseForBothTitles_CollapsesToOne()
    {
        var request = new MovieRequest { Title = "Spirited Away", AlternateTitles = ["Sen to Chihiro"] };
        var grabber = new TorrentGrabber([new SameReleaseProvider("p")]);

        var result = await grabber.SearchRankedAsync(request);

        Assert.Single(result);
    }

    // Concurrency cap: across a 3-title × 3-provider fan-out (9 queries), no more than
    // MaxConcurrentSearches run at once.
    [Fact]
    public async Task Fanout_NeverExceedsMaxConcurrentSearches()
    {
        var tracker = new ConcurrencyTracker();
        var providers = new ITorrentProvider[]
        {
            new ProbeProvider("p1", tracker, delayMs: 40),
            new ProbeProvider("p2", tracker, delayMs: 40),
            new ProbeProvider("p3", tracker, delayMs: 40),
        };
        var request = new MovieRequest { Title = "T1", AlternateTitles = ["T2", "T3"] };

        var grabber = new TorrentGrabber(providers, options: new GrabberOptions { MaxConcurrentSearches = 2 });
        await grabber.SearchAsync(request);

        Assert.Equal(9, tracker.Started);          // 3 providers × 3 titles all launched
        Assert.True(tracker.Max <= 2, $"peak concurrency was {tracker.Max}, expected <= 2");
    }

    // Quick-grab cuts the whole fan-out: a fast clearing hit cancels every remaining
    // title × provider query, so far fewer than titles × providers complete.
    [Fact]
    public async Task QuickGrab_CancelsRemainingTitleProviderQueries()
    {
        var tracker = new ConcurrencyTracker();
        var fast = new ProbeProvider("fast", tracker, delayMs: 0,
            release: r => Res($"{r.Title} 1080p BluRay x264", HashFor("fast" + r.Title), 100, "fast"));
        var slow1 = new ProbeProvider("slow1", tracker, delayMs: 10_000);
        var slow2 = new ProbeProvider("slow2", tracker, delayMs: 10_000);

        var request = new MovieRequest { Title = "Spirited Away", AlternateTitles = ["Sen to Chihiro"] };
        var grabber = new TorrentGrabber(
            [fast, slow1, slow2],
            options: new GrabberOptions { QuickGrabScore = 50 });

        var result = await grabber.GrabAsync(request);

        Assert.True(result.Found);
        // 3 providers × 2 titles = 6 pairs; the clearing hit must cut the rest short.
        Assert.True(tracker.Completed < 6, $"{tracker.Completed} queries completed, expected < 6");
    }

    private static string HashFor(string title) => new(title.Where(char.IsLetterOrDigit).ToArray());

    private static ProviderCapabilities MovieCaps() => new()
    {
        SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv, MediaType.Music },
    };

    // Counts how many times it was queried — proves the no-alternates fan-out is one call.
    private sealed class CountingProvider(string name, IReadOnlyList<TorrentResult> items) : ITorrentProvider
    {
        public int Calls;
        public string Name => name;
        public ProviderCapabilities Capabilities { get; } = MovieCaps();

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(items);
        }
    }

    // Returns a release named after the query term, so each distinct title yields a distinct hit.
    private sealed class QueryTermProvider(string name) : ITorrentProvider
    {
        public string Name => name;
        public ProviderCapabilities Capabilities { get; } = MovieCaps();

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TorrentResult>>([Res($"{request.Title} 1080p BluRay", HashFor(request.Title), 10, name)]);
    }

    // Returns the identical release regardless of the query term — dedupe must collapse it.
    private sealed class SameReleaseProvider(string name) : ITorrentProvider
    {
        public string Name => name;
        public ProviderCapabilities Capabilities { get; } = MovieCaps();

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TorrentResult>>([Res("Spirited Away 1080p BluRay", "SAME", 10, name)]);
    }

    private sealed class ConcurrencyTracker
    {
        private readonly object _lock = new();
        public int Current;
        public int Max;
        public int Started;
        public int Completed;

        public void Enter() { lock (_lock) { Started++; Current++; if (Current > Max) Max = Current; } }
        public void Exit() { lock (_lock) { Current--; } }
        public void Complete() { lock (_lock) { Completed++; } }
    }

    // Records peak simultaneous in-flight queries and how many ran to completion.
    private sealed class ProbeProvider(
        string name, GrabberTests.ConcurrencyTracker tracker, int delayMs,
        Func<MediaRequest, TorrentResult>? release = null) : ITorrentProvider
    {
        public string Name => name;
        public ProviderCapabilities Capabilities { get; } = MovieCaps();

        public async Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        {
            tracker.Enter();
            try
            {
                if (delayMs > 0)
                    await Task.Delay(delayMs, cancellationToken);
                var res = release?.Invoke(request) ?? Res($"{request.Title} 1080p BluRay", HashFor(name + request.Title), 10, name);
                tracker.Complete();
                return [res];
            }
            finally
            {
                tracker.Exit();
            }
        }
    }

    // --- Stubs -------------------------------------------------------------

    private sealed class ListProvider(string name, IReadOnlyList<TorrentResult> items, int delayMs = 0) : ITorrentProvider
    {
        public string Name => name;

        public ProviderCapabilities Capabilities { get; } = new()
        {
            SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv, MediaType.Music },
        };

        public async Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken);
            return items;
        }
    }

    /// <summary>Debrid stub that reports a fixed set of info hashes as cached.</summary>
    private sealed class CachedDebrid(params string[] cachedHashes) : IDebridService, ICachedAvailabilityChecker
    {
        private readonly HashSet<string> _cached = new(cachedHashes, StringComparer.OrdinalIgnoreCase);

        public string Name => "cached-stub";

        public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
            => Task.FromResult(DebridResult.Failed(Name, "not used"));

        public Task<IReadOnlySet<string>> GetCachedHashesAsync(IEnumerable<string> infoHashes, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(infoHashes.Where(_cached.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class ThrowingProvider(string name) : ITorrentProvider
    {
        public string Name => name;

        public ProviderCapabilities Capabilities { get; } = new()
        {
            SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv, MediaType.Music },
        };

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider exploded");
    }

    /// <summary>A provider whose Name getter itself throws — plugin-authored code can fail
    /// on ANY member, not just SearchAsync. Also fails SearchAsync so its identity is
    /// unavoidably read from the error path (QueryProviderAsync's catch blocks).</summary>
    private sealed class NameThrowingProvider : ITorrentProvider
    {
        public string Name => throw new InvalidOperationException("Name exploded");

        public ProviderCapabilities Capabilities { get; } = new()
        {
            SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv, MediaType.Music },
        };

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider exploded too");
    }
}
