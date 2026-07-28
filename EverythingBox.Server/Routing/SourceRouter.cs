using System.Diagnostics.CodeAnalysis;
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Routing;

/// <summary>
/// Every id the server emits is "{sourceKey}:{payload}". The payload is opaque —
/// each source chooses its own encoding, and may use ':' freely inside it.
/// </summary>
public sealed class SourceRouter
{
    private readonly Dictionary<string, IMediaSource> _sources;

    /// <summary>
    /// Two independently-authored plugins can plausibly pick the same source key (e.g. both
    /// choosing "local") — <see cref="EverythingBox.Server.Plugins.PluginHost"/> only dedupes
    /// plugin keys and catalog keys WITHIN one plugin, nothing dedupes source keys ACROSS
    /// plugins. Building this dictionary
    /// used to be a plain ToDictionary, which throws ArgumentException on a duplicate key —
    /// and because Program.cs resolves SourceRouter eagerly at startup, that crashed the whole
    /// server with a message that didn't even name the offending plugin. Keep the first
    /// registration, log the conflict, and drop the duplicate — consistent with every other
    /// plugin failure mode being contained rather than fatal.
    /// </summary>
    public SourceRouter(IEnumerable<IMediaSource> sources, ILogger<SourceRouter>? log = null)
    {
        log ??= NullLogger<SourceRouter>.Instance;
        _sources = new Dictionary<string, IMediaSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            if (_sources.TryGetValue(source.Key, out var existing))
            {
                log.LogError(
                    "Two sources registered the same key '{Key}' — '{Kept}' was registered first and is kept; " +
                    "the later registration from '{Dropped}' is dropped. Source keys must be unique across every " +
                    "installed plugin; rename one of them.",
                    source.Key, existing.GetType().FullName, source.GetType().FullName);
                continue;
            }

            _sources.Add(source.Key, source);
        }
    }

    public IReadOnlyCollection<IMediaSource> Sources => _sources.Values;

    public static string Prefix(string key, string id) => $"{key}:{id}";

    public bool TryResolve(
        string? prefixedId,
        [NotNullWhen(true)] out IMediaSource? source,
        [NotNullWhen(true)] out string? payload)
    {
        source = null;
        payload = null;

        if (string.IsNullOrEmpty(prefixedId)) return false;

        var separator = prefixedId.IndexOf(':');
        if (separator <= 0) return false;   // no separator, or an empty key

        if (!_sources.TryGetValue(prefixedId[..separator], out var found)) return false;

        source = found;
        payload = prefixedId[(separator + 1)..];
        return true;
    }
}
