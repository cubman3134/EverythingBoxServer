using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server;

/// <summary>
/// Shared helper for logging about a plugin-authored <see cref="IMediaSource"/> after
/// something about it has already gone wrong. <see cref="IMediaSource.Key"/> is itself
/// plugin-authored code and can throw — including from inside a catch block that is
/// already handling that same source misbehaving elsewhere. A log call must never throw,
/// so this never does: it falls back to the runtime type when Key is unavailable.
/// </summary>
internal static class PluginDiagnostics
{
    public static string SafeLabel(IMediaSource? source)
    {
        if (source is null) return "<null source>";

        try { return source.Key; }
        catch { return source.GetType().FullName ?? "<unknown source>"; }
    }

    /// <summary>Same guarded pattern as the <see cref="IMediaSource"/> overload, for
    /// an <see cref="IMetadataSource"/> — its <see cref="IMetadataSource.Name"/> is
    /// plugin-authored code and can throw just as readily as <c>Key</c> can.</summary>
    public static string SafeLabel(IMetadataSource? source)
    {
        if (source is null) return "<null source>";

        try { return source.Name; }
        catch { return source.GetType().FullName ?? "<unknown source>"; }
    }
}
