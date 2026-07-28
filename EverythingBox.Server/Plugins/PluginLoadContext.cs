using System.Reflection;
using System.Runtime.Loader;

namespace EverythingBox.Server.Plugins;

/// <summary>
/// One load context per plugin directory, so plugins cannot collide with each other's
/// dependencies. The contract assemblies are deliberately NOT loaded here: returning
/// null defers them to the default context, which is what makes a plugin's
/// IMediaSource the same type as the host's.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedAssemblies =
    [
        "EverythingBox.Server.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(entryAssemblyPath), isCollectible: false)
        => _resolver = new AssemblyDependencyResolver(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name && SharedAssemblies.Contains(name, StringComparer.Ordinal))
            return null; // default context wins — do not duplicate the contract

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
}
