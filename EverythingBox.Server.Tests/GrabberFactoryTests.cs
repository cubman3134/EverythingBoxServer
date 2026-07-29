using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

file sealed class FakeIndexer(string name) : ITorrentProvider
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
        => Task.FromResult<IReadOnlyList<TorrentResult>>([]);
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
}
