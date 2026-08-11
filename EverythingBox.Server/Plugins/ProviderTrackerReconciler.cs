using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.Plugins;

/// <summary>
/// At most one provider tracker applies across the whole server (it orders ONE provider list).
/// <see cref="PluginRegistry.AddProviderTracker"/> already refuses a second registration within
/// one plugin, but two DIFFERENT plugins each registering their own is a conflict no single
/// registry can see (each plugin gets a fresh registry). Reconciled here: the first plugin in
/// load order wins; every later one is logged by key and dropped rather than silently overriding it.
/// </summary>
internal static class ProviderTrackerReconciler
{
    public static IProviderPerformanceTracker? Resolve(IReadOnlyList<LoadedPlugin> plugins, ILogger log)
    {
        var withTracker = plugins.Where(p => p.ProviderTracker is not null).ToList();
        if (withTracker.Count > 1)
        {
            log.LogWarning(
                "{Count} plugins each registered a provider tracker ({Keys}); only '{Winner}' (first in load order) is used.",
                withTracker.Count, string.Join(", ", withTracker.Select(p => p.Key)), withTracker[0].Key);
        }
        return withTracker.Count > 0 ? withTracker[0].ProviderTracker : null;
    }
}
