using System.Text.Json;

namespace EverythingBox.Server.Tests;

public class ServerConfigTests
{
    [Fact]
    public void A_default_ServerConfig_ships_with_no_indexers_and_no_debrid()
    {
        // The project's stated purpose in concrete form: nothing pre-configured out of the box.
        var config = new ServerConfig();

        Assert.Empty(config.Indexers);
        Assert.Null(config.Debrid);
        Assert.Null(config.DownloadClient);
        Assert.NotNull(config.Ranking);
    }

    [Fact]
    public void Load_with_no_config_file_yields_the_empty_defaults()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ebs-no-config-" + Guid.NewGuid().ToString("N") + ".json");
        var previous = Environment.GetEnvironmentVariable("EBS_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("EBS_CONFIG", missing);
            var config = ServerConfig.Load();

            Assert.Empty(config.Indexers);
            Assert.Null(config.Debrid);
            Assert.Null(config.DownloadClient);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_CONFIG", previous);
        }
    }

    [Fact]
    public void A_full_config_round_trips_indexers_debrid_download_client_and_ranking()
    {
        var path = Path.Combine(Path.GetTempPath(), "ebs-config-" + Guid.NewGuid().ToString("N") + ".json");
        var previous = Environment.GetEnvironmentVariable("EBS_CONFIG");
        try
        {
            File.WriteAllText(path, """
                {
                  "Indexers": [ { "Name": "test", "BaseUrl": "http://localhost:9696/1/api", "ApiKey": "k" } ],
                  "Debrid": { "Provider": "torbox", "ApiKey": "k" },
                  "DownloadClient": { "Kind": "qbittorrent", "BaseUrl": "http://localhost:8080", "Username": "", "Password": "" },
                  "Ranking": { "MinSeeders": 5, "BannedTerms": ["CAM"] }
                }
                """);
            Environment.SetEnvironmentVariable("EBS_CONFIG", path);

            var config = ServerConfig.Load();

            var indexer = Assert.Single(config.Indexers);
            Assert.Equal("test", indexer.Name);
            Assert.Equal("http://localhost:9696/1/api", indexer.BaseUrl);

            Assert.Equal("torbox", config.Debrid?.Provider);
            Assert.Equal("qbittorrent", config.DownloadClient?.Kind);
            Assert.Equal(5, config.Ranking.MinSeeders);
            Assert.Contains("CAM", config.Ranking.BannedTerms);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EBS_CONFIG", previous);
            File.Delete(path);
        }
    }

    [Fact]
    public void The_example_config_ships_with_an_empty_Indexers_array_and_no_debrid()
    {
        var path = Path.Combine(RepositoryRoot(), "EverythingBox.Server", "everythingbox-server.example.json");
        var json = File.ReadAllText(path);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var config = JsonSerializer.Deserialize<ServerConfig>(json, options);

        Assert.NotNull(config);
        Assert.Empty(config!.Indexers);
        Assert.True(string.IsNullOrEmpty(config.Debrid?.ApiKey));
    }

    [Fact]
    public void Self_download_is_off_in_a_stock_config()
    {
        // Downloading joins a BitTorrent swarm from the user's own IP. That must be
        // something they turn on, not something an upgrade turns on for them.
        Assert.False(new ServerConfig().Download.Enabled);
    }

    [Fact]
    public void Download_has_a_size_cap_and_a_timeout_by_default()
    {
        var download = new ServerConfig().Download;
        Assert.True(download.MaxSizeMB > 0, "an uncapped fallback would try to fetch anything");
        Assert.True(download.TimeoutSeconds > 0, "an unbounded wait would hold a request open on a dead swarm");
    }

    [Fact]
    public void Download_is_never_null_even_when_the_config_file_omits_it()
    {
        // Every existing config file predates this key; reading one must not NRE.
        var config = ServerConfig.FromJson("""{ "Listen": "http://0.0.0.0:7000" }""");
        Assert.NotNull(config.Download);
        Assert.False(config.Download.Enabled);
    }

    [Fact]
    public void Ranking_binds_a_preferred_release_group_list()
    {
        var config = ServerConfig.FromJson(
            """{ "Ranking": { "PreferredReleaseGroups": ["GroupA", "GroupB"] } }""");

        Assert.Equal(new[] { "GroupA", "GroupB" }, config.Ranking.PreferredReleaseGroups);
    }

    [Fact]
    public void A_plugin_section_binds_even_when_its_key_casing_differs()
    {
        // System.Text.Json drops the Plugins dictionary's OrdinalIgnoreCase comparer on
        // deserialize; the section must still bind when the user's casing differs from the key.
        var config = ServerConfig.FromJson("""{ "Plugins": { "MyPlugin": { "Enabled": true } } }""");

        var section = config.PluginSection<CaseTestSection>("myplugin");

        Assert.NotNull(section);
        Assert.True(section!.Enabled);
    }

    private sealed record CaseTestSection(bool Enabled);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
