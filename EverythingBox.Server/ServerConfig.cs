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

    public GrabberConfig Grabber { get; set; } = new();

    /// <summary>
    /// Fetching an uncached release ourselves rather than waiting on debrid. OFF by
    /// default: unlike every other path here, this joins a BitTorrent swarm from your
    /// own IP address, so it is something you switch on deliberately.
    /// </summary>
    public DownloadConfig Download { get; set; } = new();

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

    /// <summary>Deserializes a ServerConfig from a JSON string, using the same options as Load.</summary>
    public static ServerConfig FromJson(string json)
    {
        return JsonSerializer.Deserialize<ServerConfig>(json, Json) ?? new ServerConfig();
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

/// <summary>Grabber tuning knobs. Ships with engine-neutral defaults — an absent
/// <c>Grabber</c> block in the config file deserializes to these same values, so
/// omitting the section changes nothing about existing behavior.</summary>
public sealed class GrabberConfig
{
    /// <summary>Early-exit search once a candidate scores at least this; null = search all
    /// providers and pick the overall best (the engine default).</summary>
    public double? QuickGrabScore { get; set; }

    /// <summary>Per-provider search timeout (seconds). Default 30.</summary>
    public int ProviderTimeoutSeconds { get; set; } = 30;

    /// <summary>Float debrid-cached releases to the front. Default false.</summary>
    public bool PreferCachedReleases { get; set; }
}

public sealed class DownloadConfig
{
    /// <summary>Off by default. See the note on <see cref="ServerConfig.Download"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Releases larger than this are never self-downloaded — a fallback should
    /// finish while the user is still interested.</summary>
    public int MaxSizeMB { get; set; } = 2048;

    /// <summary>Gives up on a stalled or seedless swarm instead of holding the request open.</summary>
    public int TimeoutSeconds { get; set; } = 600;
}
