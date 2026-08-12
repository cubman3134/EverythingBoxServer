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
        registry.AddSource(new RomLibrarySource(config.Roms, context.Loggers.CreateLogger<RomLibrarySource>()));
    }
}
