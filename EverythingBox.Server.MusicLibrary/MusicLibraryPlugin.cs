using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.MusicLibrary;

public sealed class MusicLibraryPlugin : IPlugin
{
    public string Key => "musiclib";
    public string DisplayName => "Music Library";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<MusicLibraryConfig>() ?? new MusicLibraryConfig();
        var cache = new FileResolverCache(Path.Combine(context.CacheDirectory, "meta"), 16L * 1024 * 1024);
        var coverDir = Path.Combine(context.CacheDirectory, "covers");
        registry.AddSource(new MusicLibrarySource(
            config.Roots, coverDir, cache,
            context.Loggers.CreateLogger<MusicLibrarySource>()));
    }
}
