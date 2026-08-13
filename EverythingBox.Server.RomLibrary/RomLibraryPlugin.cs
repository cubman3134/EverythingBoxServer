using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.RomLibrary;

public sealed class RomLibraryPlugin : IPlugin
{
    public string Key => "romlib";
    public string DisplayName => "ROM Library";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<RomLibraryConfig>() ?? new RomLibraryConfig();
        var cache = new FileResolverCache(Path.Combine(context.CacheDirectory, "meta"), 16L * 1024 * 1024);
        registry.AddSource(new RomLibrarySource(config.Roms, config.GroupUpdatesAndDlc, cache, context.Loggers.CreateLogger<RomLibrarySource>()));
    }
}
