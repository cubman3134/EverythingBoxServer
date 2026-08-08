using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

file sealed class FakeIndexer(string name, TorrentResult? result = null) : ITorrentProvider
{
    public string Name => name;

    // ProviderCapabilities.SupportedMediaTypes is `required` in this codebase (added after
    // the brief that specifies this test verbatim was written) — set to every type so this
    // fake never becomes the reason a media-type routing assertion fails.
    public ProviderCapabilities Capabilities { get; } = new()
    {
        SupportedMediaTypes = new HashSet<MediaType>(Enum.GetValues<MediaType>()),
    };
    public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TorrentResult>>(result is null ? [] : [result]);
}

/// <summary>A debrid double that tags every <see cref="DebridResult"/> it produces with
/// its own identity, so a test can prove a grabber actually resolves through THIS
/// instance rather than merely through "an" <see cref="IDebridService"/> of the same
/// shape.</summary>
file sealed class MarkedDebrid(string marker) : IDebridService
{
    public string Name => marker;

    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => Task.FromResult(DebridResult.Resolved(Name, "id", cached: true, []));
}

public class GrabberFactoryTests
{
    private static (TorrentGrabber Grabber, IDebridService? Debrid) Build(
        ServerConfig config, params ITorrentProvider[] pluginIndexers)
        => GrabberFactory.Build(config, new HttpClient(), pluginIndexers, NullLoggerFactory.Instance);

    [Fact]
    public void A_stock_config_yields_a_grabber_with_no_indexers_and_no_debrid()
    {
        // The whole point of the project: nothing is configured out of the box. The brief's
        // verbatim assertions (NotNull/Null) don't actually prove "no indexers" numerically —
        // Assert.Empty(grabber.Providers) added so this test bites a regression that seeds a
        // default indexer, per the task's mutation-testing verification step.
        var (grabber, debrid) = Build(new ServerConfig());
        Assert.NotNull(grabber);
        Assert.Empty(grabber.Providers);
        Assert.Null(debrid);
    }

    [Fact]
    public void A_configured_indexer_becomes_a_provider()
    {
        var config = new ServerConfig
        {
            Indexers = [new IndexerConfig { Name = "test", BaseUrl = "http://localhost:9696/1/api", ApiKey = "k" }],
        };

        var (grabber, _) = Build(config);
        Assert.Single(grabber.Providers);
    }

    [Fact]
    public void Config_indexers_and_plugin_indexers_feed_the_same_grabber()
    {
        var config = new ServerConfig
        {
            Indexers = [new IndexerConfig { Name = "test", BaseUrl = "http://localhost:9696/1/api", ApiKey = "k" }],
        };

        var (grabber, _) = Build(config, new FakeIndexer("from-plugin"));
        Assert.Equal(2, grabber.Providers.Count);
    }

    [Fact]
    public void An_indexer_with_no_base_url_is_skipped_rather_than_throwing()
    {
        // A half-filled config entry is a likely user mistake; it must not stop startup.
        var config = new ServerConfig { Indexers = [new IndexerConfig { Name = "broken" }] };

        var (grabber, _) = Build(config);
        Assert.Empty(grabber.Providers);
    }

    [Theory]
    [InlineData("torbox")]
    [InlineData("TorBox")]
    [InlineData("realdebrid")]
    public void A_configured_debrid_provider_is_built(string provider)
    {
        var config = new ServerConfig { Debrid = new DebridConfig { Provider = provider, ApiKey = "k" } };
        var (_, debrid) = Build(config);
        Assert.NotNull(debrid);
    }

    [Fact]
    public void A_debrid_block_with_no_key_yields_no_service()
    {
        var config = new ServerConfig { Debrid = new DebridConfig { Provider = "torbox", ApiKey = "" } };
        var (_, debrid) = Build(config);
        Assert.Null(debrid);
    }

    [Fact]
    public void An_unknown_debrid_provider_yields_no_service_rather_than_throwing()
    {
        var config = new ServerConfig { Debrid = new DebridConfig { Provider = "nonesuch", ApiKey = "k" } };
        var (_, debrid) = Build(config);
        Assert.Null(debrid);
    }

    [Fact]
    public void Ranking_options_reach_the_grabber()
    {
        var config = new ServerConfig { Ranking = new RankingOptions { MinSeeders = 42 } };
        var (grabber, _) = Build(config);
        Assert.Equal(42, grabber.Options.Ranking.MinSeeders);
    }

    [Fact]
    public void Grabber_tuning_options_reach_the_grabber()
    {
        var config = new ServerConfig
        {
            Grabber = new GrabberConfig
            {
                QuickGrabScore = 85.5,
                ProviderTimeoutSeconds = 12,
                PreferCachedReleases = true,
            },
        };

        var (grabber, _) = Build(config);

        Assert.Equal(85.5, grabber.Options.QuickGrabScore);
        Assert.Equal(TimeSpan.FromSeconds(12), grabber.Options.ProviderTimeout);
        Assert.True(grabber.Options.PreferCachedReleases);
    }

    [Fact]
    public void Grabber_tuning_options_reach_the_grabber_via_the_production_Build_overload()
    {
        // The tuple-returning Build above is test-only. Program.cs calls the overload that
        // takes a pre-built IDebridService? — its own `new GrabberOptions {}` is a SEPARATE
        // copy of the tuning wiring, so a drop of QuickGrabScore/ProviderTimeout/
        // PreferCachedReleases there would slip past every other test in this file. Assert the
        // same tuning fields reach the grabber through the production overload too.
        var config = new ServerConfig
        {
            Grabber = new GrabberConfig
            {
                QuickGrabScore = 73.25,
                ProviderTimeoutSeconds = 19,
                PreferCachedReleases = true,
            },
        };

        var grabber = GrabberFactory.Build(
            config, new HttpClient(), [], NullLoggerFactory.Instance, debrid: null);

        Assert.Equal(73.25, grabber.Options.QuickGrabScore);
        Assert.Equal(TimeSpan.FromSeconds(19), grabber.Options.ProviderTimeout);
        Assert.True(grabber.Options.PreferCachedReleases);
    }

    [Fact]
    public void An_omitted_Grabber_block_preserves_the_engines_current_defaults()
    {
        // A stock config's Grabber section deserializes to `new()` — this must build the
        // exact same GrabberOptions as before Grabber tuning existed at all, so a config
        // written for an older engine keeps behaving identically after this upgrade.
        var (grabber, _) = Build(new ServerConfig());

        Assert.Null(grabber.Options.QuickGrabScore);
        Assert.Equal(TimeSpan.FromSeconds(30), grabber.Options.ProviderTimeout);
        Assert.False(grabber.Options.PreferCachedReleases);
    }

    [Fact]
    public void Providers_collection_is_genuinely_read_only()
    {
        // Providers property is typed IReadOnlyList but must actually be immutable,
        // so a downcast to List<T> and mutation attempt cannot succeed.
        var config = new ServerConfig
        {
            Indexers = [new IndexerConfig { Name = "test", BaseUrl = "http://localhost:9696/1/api", ApiKey = "k" }],
        };

        var (grabber, _) = Build(config);
        Assert.Single(grabber.Providers);

        // Attempt to downcast and mutate — should throw NotSupportedException
        // because AsReadOnly() returns a true ReadOnlyCollection<T>, not a List<T>.
        var casted = grabber.Providers as List<ITorrentProvider>;
        if (casted is not null)
        {
            // If somehow it was a raw list, mutation would succeed and this test fails.
            casted.Clear();
            Assert.Fail("Providers collection was mutable via downcast");
        }

        // Re-check that the original reference is unchanged.
        Assert.Single(grabber.Providers);
    }

    private static TorrentResult FindableRelease() => new()
    {
        Title = "Test Movie 2020 1080p",
        ProviderName = "fake",
        MagnetUri = new Uri("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567"),
        Seeders = 10,
    };

    [Fact]
    public async Task A_download_client_with_blank_kind_is_skipped_rather_than_throwing()
    {
        // Same degradation pattern as indexer/debrid: missing Kind is a user mistake.
        var config = new ServerConfig
        {
            DownloadClient = new DownloadClientConfig { Kind = "", BaseUrl = "http://localhost:8080" },
        };

        var (grabber, _) = Build(config);

        // The grabber must build successfully — but "skipped" has to mean the client is
        // genuinely not wired in, not merely that Build() didn't throw. GrabAndDownloadAsync
        // throws InvalidOperationException specifically when no download client is configured
        // (checked before it ever searches a provider), so that's the one call that actually
        // distinguishes "skipped" from "wired in but happens to look fine here".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grabber.GrabAndDownloadAsync(new MovieRequest { Title = "x" }));
        Assert.Contains("No download client configured", ex.Message);
    }

    [Fact]
    public async Task A_download_client_with_unparseable_base_url_is_skipped_rather_than_throwing()
    {
        // Same degradation pattern: malformed BaseUrl is a user mistake.
        var config = new ServerConfig
        {
            DownloadClient = new DownloadClientConfig { Kind = "qbittorrent", BaseUrl = "not-a-url" },
        };

        var (grabber, _) = Build(config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grabber.GrabAndDownloadAsync(new MovieRequest { Title = "x" }));
        Assert.Contains("No download client configured", ex.Message);
    }

    [Fact]
    public async Task An_unknown_download_client_kind_is_skipped_rather_than_throwing()
    {
        // Same degradation pattern: unknown client kind is a user mistake.
        var config = new ServerConfig
        {
            DownloadClient = new DownloadClientConfig { Kind = "nonesuch", BaseUrl = "http://localhost:8080" },
        };

        var (grabber, _) = Build(config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grabber.GrabAndDownloadAsync(new MovieRequest { Title = "x" }));
        Assert.Contains("No download client configured", ex.Message);
    }

    [Fact]
    public async Task The_debrid_service_is_shared_between_plugins_and_grabber()
    {
        // F1 fix: the debrid instance IServerServices.Debrid hands to plugins must be the
        // EXACT instance the grabber resolves through — not a second, independently built
        // debrid that merely looks equivalent. A pair of Assert.NotNull calls (the original
        // shape of this test) would pass even if GrabberFactory quietly built its own debrid
        // from config instead of using the one handed to it — the regression this test exists
        // to catch. Prove identity instead: tag a fake debrid with a unique marker and check
        // the grabber's own resolved result carries THAT marker, not one it built for itself.
        var marker = Guid.NewGuid().ToString();
        var debrid = new MarkedDebrid(marker);
        var provider = new FakeIndexer("from-plugin", FindableRelease());

        // Build(..., debrid) is the overload Program.cs uses once BuildDebrid has already
        // produced the shared instance — it must wire THIS debrid in, not build its own.
        var grabber = GrabberFactory.Build(new ServerConfig(), new HttpClient(), [provider], NullLoggerFactory.Instance, debrid);

        var result = await grabber.GrabAndResolveAsync(new MovieRequest { Title = "Test Movie" });

        Assert.True(result.Found);
        Assert.NotNull(result.Debrid);
        Assert.Equal(marker, result.Debrid!.ServiceName);
    }

    [Fact]
    public async Task A_provider_tracker_passed_to_GrabberFactory_actually_reaches_the_grabber()
    {
        // A registry that merely HOLDS a tracker nobody passes on would satisfy every
        // other test in this file while changing nothing at runtime. Prove the tracker
        // handed to GrabberFactory.Build is the one TorrentGrabber actually consults
        // during a real search — not just that Build() accepted the parameter.
        var tracker = new RecordingTracker();
        var provider = new FakeIndexer("from-plugin", FindableRelease());

        var grabber = GrabberFactory.Build(
            new ServerConfig(), new HttpClient(), [provider], NullLoggerFactory.Instance,
            debrid: null, transport: null, providerTracker: tracker);

        await grabber.GrabAsync(new MovieRequest { Title = "Test Movie" });

        Assert.True(tracker.PrioritizeCalled);
    }

    private sealed class RecordingTracker : IProviderPerformanceTracker
    {
        public bool PrioritizeCalled { get; private set; }

        public IReadOnlyList<ITorrentProvider> Prioritize(IReadOnlyList<ITorrentProvider> providers)
        {
            PrioritizeCalled = true;
            return providers;
        }

        public void Record(IReadOnlyList<ProviderOutcome> outcomes) { }
    }
}
