using System.Reflection;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

public class ServerApiTests
{
    /// <summary>
    /// The whole compatibility mechanism depends on VersionString being a genuine
    /// compile-time constant (a C# `const`), not a `static readonly` field. A plugin's
    /// reference to a `const` is baked into the PLUGIN's own assembly at build time; a
    /// reference to a `static readonly` field is resolved at runtime against whatever
    /// Abstractions the host loads — which, because PluginLoadContext defers Abstractions
    /// to the default context, is always the HOST's copy. That would make every
    /// well-behaved plugin compare the host's version against itself and always pass,
    /// even a stale plugin built against an old API. IsLiteral is how reflection
    /// distinguishes "const" (a literal baked into IL at every call site) from a field.
    /// </summary>
    [Fact]
    public void VersionString_is_a_genuine_compile_time_constant()
    {
        var field = typeof(ServerApi).GetField(nameof(ServerApi.VersionString), BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        Assert.True(field!.IsLiteral, "ServerApi.VersionString must be a `const`, not a `static readonly` field.");
    }

    [Fact]
    public void Current_matches_VersionString()
    {
        Assert.Equal(new Version(ServerApi.VersionString), ServerApi.Current);
    }

    [Fact]
    public void Same_major_older_or_equal_minor_is_compatible()
    {
        Assert.True(ServerApi.IsCompatible(new Version(ServerApi.Current.Major, 0)));
        Assert.True(ServerApi.IsCompatible(ServerApi.Current));
    }

    [Fact]
    public void Different_major_is_incompatible()
    {
        Assert.False(ServerApi.IsCompatible(new Version(ServerApi.Current.Major + 1, 0)));
    }

    [Fact]
    public void Newer_minor_is_incompatible()
    {
        Assert.False(ServerApi.IsCompatible(new Version(ServerApi.Current.Major, ServerApi.Current.Minor + 1)));
    }
}
