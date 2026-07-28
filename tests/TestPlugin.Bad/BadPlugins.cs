using EverythingBox.Server.Abstractions;

namespace TestPlugin.Bad;

/// <summary>Declares an API version from a future major — must be refused.</summary>
public sealed class FutureApiPlugin : IPlugin
{
    public string Key => "future";
    public string DisplayName => "Future API Plugin";
    public Version ApiVersion => new(ServerApi.Version.Major + 1, 0);

    public void Configure(IPluginRegistry registry, IPluginContext context)
        => throw new InvalidOperationException("must never be called — the version gate runs first");
}

/// <summary>Throws during registration — must be skipped without taking the host down.</summary>
public sealed class ThrowingPlugin : IPlugin
{
    public string Key => "throwing";
    public string DisplayName => "Throwing Plugin";
    public Version ApiVersion => ServerApi.Version;

    public void Configure(IPluginRegistry registry, IPluginContext context)
        => throw new InvalidOperationException("boom");
}
