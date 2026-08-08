using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// IServerServices.CreateDebrid (API 1.8): the factory that lets a plugin build a debrid
/// from a provider name + key it obtained itself, without the plugin ever seeing the
/// concrete TorBoxService/RealDebridService types (those live in Core, out of a plugin's
/// reach — only Abstractions is). ServerServices.CreateDebrid must delegate to the exact
/// same provider→service switch GrabberFactory's config-driven debrid path uses.
/// </summary>
public class ServerServicesCreateDebridTests
{
    private static IServerServices Services() =>
        new ServerServices(
            grabber: null!,
            debrid: null,
            files: null!,
            http: new HttpClient(),
            loggerFactory: NullLoggerFactory.Instance);

    [Fact]
    public void Torbox_builds_a_TorBox_service()
    {
        var result = Services().CreateDebrid("torbox", "K");

        Assert.NotNull(result);
        Assert.Equal("TorBox", result!.Name);
    }

    [Fact]
    public void Provider_name_is_case_insensitive()
    {
        var result = Services().CreateDebrid("TORBOX", "K");

        Assert.NotNull(result);
        Assert.Equal("TorBox", result!.Name);
    }

    [Fact]
    public void Realdebrid_builds_a_RealDebrid_service()
    {
        var result = Services().CreateDebrid("realdebrid", "K");

        Assert.NotNull(result);
        Assert.Equal("Real-Debrid", result!.Name);
    }

    [Fact]
    public void Unknown_provider_returns_null_rather_than_throwing()
    {
        Assert.Null(Services().CreateDebrid("nope", "K"));
    }

    [Fact]
    public void Blank_key_returns_null()
    {
        Assert.Null(Services().CreateDebrid("torbox", ""));
    }

    [Fact]
    public void Whitespace_key_returns_null()
    {
        Assert.Null(Services().CreateDebrid("torbox", "   "));
    }

    /// <summary>
    /// The refactor that introduced GrabberFactory.CreateDebrid must not change the
    /// config-driven path's behaviour — proven directly in GrabberFactoryTests
    /// (A_configured_debrid_provider_is_built etc.), which still pass unmodified. This test
    /// additionally proves the two paths agree on the SAME instance shape (same Name) for
    /// the same provider, which is only true if they share one switch.
    /// </summary>
    [Fact]
    public void Config_path_and_plugin_factory_path_produce_the_same_service_identity()
    {
        var config = new ServerConfig { Debrid = new DebridConfig { Provider = "torbox", ApiKey = "K" } };
        var configBuilt = GrabberFactory.BuildDebrid(config, new HttpClient(), NullLoggerFactory.Instance);

        var pluginBuilt = Services().CreateDebrid("torbox", "K");

        Assert.NotNull(configBuilt);
        Assert.NotNull(pluginBuilt);
        Assert.Equal(configBuilt!.Name, pluginBuilt!.Name);
    }
}
