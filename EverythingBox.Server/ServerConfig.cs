using System.Text.Json;
using System.Text.Json.Serialization;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

public sealed class ServerConfig
{
    public string Listen { get; set; } = "http://0.0.0.0:7000";

    /// <summary>REQUIRED when reachable from the internet. Becomes a URL path prefix.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Defaults to a "plugins" folder next to the executable.</summary>
    public string? PluginsDirectory { get; set; }

    /// <summary>Where generated files are cached and served from.</summary>
    public string? FilesCacheDir { get; set; }

    public ManifestConfig Manifest { get; set; } = new();

    /// <summary>One opaque section per plugin, keyed by plugin key.</summary>
    public Dictionary<string, JsonElement> Plugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Torznab endpoints. Ships EMPTY — this server configures no indexer for you.</summary>
    public List<IndexerConfig> Indexers { get; set; } = [];

    public DebridConfig? Debrid { get; set; }

    public DownloadClientConfig? DownloadClient { get; set; }

    public RankingOptions Ranking { get; set; } = new();

    public string ResolvedPluginsDirectory =>
        Environment.GetEnvironmentVariable("EBS_PLUGINS_DIR") is { Length: > 0 } fromEnv ? fromEnv
        : !string.IsNullOrWhiteSpace(PluginsDirectory) ? PluginsDirectory!
        : Path.Combine(AppContext.BaseDirectory, "plugins");

    public string ResolvedFilesCacheDir =>
        Environment.GetEnvironmentVariable("EBS_FILES_DIR") is { Length: > 0 } fromEnv ? fromEnv
        : !string.IsNullOrWhiteSpace(FilesCacheDir) ? FilesCacheDir!
        : Path.Combine(AppContext.BaseDirectory, "files");

    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("EBS_CONFIG") is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(AppContext.BaseDirectory, "everythingbox-server.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ServerConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path)) return new ServerConfig();

        try
        {
            return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json) ?? new ServerConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Binds this plugin's opaque section to its own type.</summary>
    public T? PluginSection<T>(string key) where T : class
    {
        if (!Plugins.TryGetValue(key, out var element)) return null;
        return element.Deserialize<T>(Json);
    }
}

public sealed class ManifestConfig
{
    public string Id { get; set; } = "com.everythingbox.server";
    public string Name { get; set; } = "EverythingBox Server";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "Media from the sources you configure.";
    public string Accent { get; set; } = "#3E8E7E";

    public ManifestOptions ToOptions() => new(Id, Name, Version, Description, Accent);
}

/// <summary>One Torznab indexer endpoint (e.g. a Prowlarr or Jackett instance).</summary>
public sealed class IndexerConfig
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

/// <summary>The debrid service to resolve releases through, if any.</summary>
public sealed class DebridConfig
{
    /// <summary>"torbox" or "realdebrid", matched case-insensitively.</summary>
    public string Provider { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

/// <summary>The download client to hand releases to, if any.</summary>
public sealed class DownloadClientConfig
{
    /// <summary>"qbittorrent" or "transmission", matched case-insensitively.</summary>
    public string Kind { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Category { get; set; } = "";
}
