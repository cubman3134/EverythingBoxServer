namespace EverythingBox.Server.Abstractions;

/// <summary>The plugin API version this Abstractions assembly represents.
/// A plugin declares the version it was built against; the host refuses to load
/// one it cannot satisfy.</summary>
public static class ServerApi
{
    /// <summary>
    /// A COMPILE-TIME constant on purpose. A plugin's reference to this is baked into the
    /// plugin's own assembly, so a plugin built against 1.0 still reports 1.0 when loaded by
    /// a 1.1 host. A static readonly field would resolve to the HOST's value at runtime,
    /// making the compatibility check compare the host against itself and always pass.
    /// </summary>
    public const string VersionString = "1.20";

    public static Version Current { get; } = new(VersionString);

    /// <summary>Same major, and a minor no newer than ours — we can satisfy a plugin
    /// built against an older minor, never a newer one.</summary>
    public static bool IsCompatible(Version pluginApiVersion) =>
        pluginApiVersion.Major == Current.Major && pluginApiVersion.Minor <= Current.Minor;
}
