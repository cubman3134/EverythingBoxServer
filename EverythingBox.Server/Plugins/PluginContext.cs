using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Plugins;

/// <summary>What a plugin gets at registration. The cache directory is created
/// before Configure runs, so a plugin can write to it immediately.</summary>
public sealed class PluginContext : IPluginContext
{
    private readonly ServerConfig _config;
    private readonly string _key;

    public PluginContext(string key, ServerConfig config, ILoggerFactory loggers, HttpClient http, string cacheRoot)
    {
        _key = key;
        _config = config;
        Loggers = loggers;
        Http = http;
        CacheDirectory = Path.Combine(cacheRoot, key);
        Directory.CreateDirectory(CacheDirectory);
    }

    public ILoggerFactory Loggers { get; }
    public HttpClient Http { get; }
    public string CacheDirectory { get; }

    public T? GetConfig<T>() where T : class => _config.PluginSection<T>(_key);
}
