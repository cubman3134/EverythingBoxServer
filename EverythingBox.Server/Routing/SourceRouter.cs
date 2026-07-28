using System.Diagnostics.CodeAnalysis;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Routing;

/// <summary>
/// Every id the server emits is "{sourceKey}:{payload}". The payload is opaque —
/// each source chooses its own encoding, and may use ':' freely inside it.
/// </summary>
public sealed class SourceRouter
{
    private readonly Dictionary<string, IMediaSource> _sources;

    public SourceRouter(IEnumerable<IMediaSource> sources)
        => _sources = sources.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

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
