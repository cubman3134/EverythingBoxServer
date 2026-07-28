using System.Text.Json;
using System.Text.Json.Serialization;

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
