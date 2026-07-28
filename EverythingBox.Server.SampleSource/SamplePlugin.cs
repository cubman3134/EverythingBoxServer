using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.SampleSource;

public sealed class SamplePlugin : IPlugin
{
    public string Key => "local";
    public string DisplayName => "Local Folder";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<LocalFolderConfig>() ?? new LocalFolderConfig();

        context.Loggers.CreateLogger<SamplePlugin>()
            .LogInformation("Serving {Count} local folder(s).", config.Folders.Count);

        registry.AddSource(new LocalFolderSource(config));
    }
}
