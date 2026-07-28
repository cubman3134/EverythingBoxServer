using EverythingBox.Server.Abstractions;

namespace TestPlugin.Dup;

/// <summary>Two plugin types sharing the same key in one assembly — only one of them
/// may survive PluginHost's duplicate-key guard, and Load must still return normally.</summary>
public sealed class DupPluginA : IPlugin
{
    public string Key => "dup";
    public string DisplayName => "Dup Plugin A";
    public Version ApiVersion => ServerApi.Version;

    public void Configure(IPluginRegistry registry, IPluginContext context) { }
}

public sealed class DupPluginB : IPlugin
{
    public string Key => "dup";
    public string DisplayName => "Dup Plugin B";
    public Version ApiVersion => ServerApi.Version;

    public void Configure(IPluginRegistry registry, IPluginContext context) { }
}
