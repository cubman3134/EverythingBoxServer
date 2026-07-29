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

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
