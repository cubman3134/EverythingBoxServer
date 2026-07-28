namespace EverythingBox.Server.Tests;

/// <summary>
/// Minor: SafeLabel's own doc comment claims it never throws, but a null source reads
/// source.Key (NRE), then falls into the catch which reads source.GetType() (NRE again,
/// escaping the catch). Not reachable through today's call sites, but worth locking down
/// given the doc comment's explicit promise.
/// </summary>
public class PluginDiagnosticsTests
{
    [Fact]
    public void SafeLabel_of_a_healthy_source_is_its_Key()
    {
        Assert.Equal("alpha", PluginDiagnostics.SafeLabel(new FakeSource("alpha")));
    }

    [Fact]
    public void SafeLabel_of_null_does_not_throw()
    {
        var label = Record.Exception(() => PluginDiagnostics.SafeLabel(null));
        Assert.Null(label);
    }

    [Fact]
    public void SafeLabel_of_null_returns_a_placeholder()
    {
        Assert.False(string.IsNullOrEmpty(PluginDiagnostics.SafeLabel(null)));
    }
}
