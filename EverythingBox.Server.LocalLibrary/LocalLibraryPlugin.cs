using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.LocalLibrary;

public sealed class LocalLibraryPlugin : IPlugin
{
    public string Key => "locallib";
    public string DisplayName => "Local Library";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<LocalLibraryConfig>() ?? new LocalLibraryConfig();
        var cache = new FileResolverCache(Path.Combine(context.CacheDirectory, "meta"), 16L * 1024 * 1024);
        registry.AddSource(new LocalLibrarySource(config.Movies, config.Series, cache, context.Loggers.CreateLogger<LocalLibrarySource>()));
    }
}
