using System.Reflection;
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Plugins;

public sealed record LoadedPlugin(
    string Key, string DisplayName, IReadOnlyCollection<IMediaSource> Sources, IReadOnlyCollection<ITorrentProvider> Indexers);

/// <summary>
/// Discovers plugins under plugins/&lt;key&gt;/ and loads each into its own context.
/// Every failure mode is contained: a plugin that will not load, declares an
/// incompatible API version, or throws while registering is logged and skipped —
/// the server still starts.
/// </summary>
public sealed class PluginHost(ILogger<PluginHost> log)
{
    public IReadOnlyList<LoadedPlugin> Load(string pluginsDirectory, Func<IPlugin, IPluginContext> contextFactory)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            log.LogInformation("No plugins directory at {Path} — running with built-in sources only.", pluginsDirectory);
            return [];
        }

        var loaded = new List<LoadedPlugin>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(pluginsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            foreach (var plugin in Instantiate(directory))
            {
                if (TryConfigure(plugin, directory, contextFactory, keys) is { } result)
                    loaded.Add(result);
            }
        }

        log.LogInformation("Loaded {Count} plugin(s): {Keys}",
            loaded.Count, loaded.Count == 0 ? "(none)" : string.Join(", ", loaded.Select(p => p.Key)));
        return loaded;
    }

    /// <summary>Every assembly in the directory is a candidate — a plugin's entry
    /// assembly is not required to be named after its folder.</summary>
    private IEnumerable<IPlugin> Instantiate(string directory)
    {
        foreach (var dll in Directory.EnumerateFiles(directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
        {
            Assembly assembly;
            try
            {
                assembly = new PluginLoadContext(dll).LoadFromAssemblyPath(dll);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                log.LogDebug("Not a loadable managed assembly, skipping: {Dll} ({Message})", dll, ex.Message);
                continue;
            }

            foreach (var type in PublicTypes(assembly, dll))
            {
                if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    log.LogError("Plugin type {Type} in {Dll} has no public parameterless constructor — skipping.", type.FullName, dll);
                    continue;
                }

                IPlugin? instance = null;
                try
                {
                    instance = (IPlugin)Activator.CreateInstance(type)!;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Could not construct plugin type {Type} in {Dll} — skipping.", type.FullName, dll);
                }

                if (instance is not null) yield return instance;
            }
        }
    }

    private Type[] PublicTypes(Assembly assembly, string dll)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            log.LogWarning("Some types in {Dll} could not be loaded; using the ones that did.", dll);
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not read types from {Dll} — skipping.", dll);
            return [];
        }
    }

    /// <summary>Every member of <paramref name="plugin"/> is plugin-authored code and
    /// can throw — the whole body below the key check is one try block so nothing it
    /// touches (ApiVersion, DisplayName, Configure) can escape and take the host down.</summary>
    private LoadedPlugin? TryConfigure(
        IPlugin plugin, string directory, Func<IPlugin, IPluginContext> contextFactory, HashSet<string> seenKeys)
    {
        string key;
        try
        {
            key = plugin.Key;
            PluginRegistry.ValidateKey(key, nameof(plugin));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Plugin in {Directory} has an invalid key — skipping.", directory);
            return null;
        }

        try
        {
            var apiVersion = plugin.ApiVersion;
            if (!ServerApi.IsCompatible(apiVersion))
            {
                log.LogError(
                    "Plugin '{Key}' targets API {PluginVersion}, this server provides {ServerVersion} — skipping. Update the plugin.",
                    key, apiVersion, ServerApi.Current);
                return null;
            }

            if (!seenKeys.Add(key))
            {
                log.LogError("Plugin key '{Key}' is already loaded — skipping the copy in {Directory}.", key, directory);
                return null;
            }

            var registry = new PluginRegistry();
            try
            {
                plugin.Configure(registry, contextFactory(plugin));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Plugin '{Key}' threw while registering — skipping it. The server is still starting.", key);
                seenKeys.Remove(key);
                return null;
            }

            var displayName = plugin.DisplayName;
            log.LogInformation("Plugin '{Key}' ({Name}) registered {Sources} source(s) and {Indexers} indexer(s).",
                key, displayName, registry.Sources.Count, registry.Indexers.Count);
            return new LoadedPlugin(key, displayName, registry.Sources, registry.Indexers);
        }
        catch (Exception ex)
        {
            seenKeys.Remove(key);
            log.LogError(ex, "Plugin '{Key}' threw while loading — skipping it. The server is still starting.", key);
            return null;
        }
    }
}
