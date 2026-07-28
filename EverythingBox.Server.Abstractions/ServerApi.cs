namespace EverythingBox.Server.Abstractions;

/// <summary>The plugin API version this Abstractions assembly represents.
/// A plugin declares the version it was built against; the host refuses to load
/// one it cannot satisfy.</summary>
public static class ServerApi
{
    public static readonly Version Version = new(1, 0);

    /// <summary>Same major, and a minor no newer than ours — we can satisfy a plugin
    /// built against an older minor, never a newer one.</summary>
    public static bool IsCompatible(Version pluginApiVersion) =>
        pluginApiVersion.Major == Version.Major && pluginApiVersion.Minor <= Version.Minor;
}
