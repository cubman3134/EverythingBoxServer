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
        registry.AddSource(new MovieLibrarySource(config.Movies, context.Loggers.CreateLogger<MovieLibrarySource>()));
    }
}
