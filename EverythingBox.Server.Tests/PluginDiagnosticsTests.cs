using EverythingBox.Server.Abstractions;

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
        var label = Record.Exception(() => PluginDiagnostics.SafeLabel((IMediaSource?)null));
        Assert.Null(label);
    }

    [Fact]
    public void SafeLabel_of_null_returns_a_placeholder()
    {
        Assert.False(string.IsNullOrEmpty(PluginDiagnostics.SafeLabel((IMediaSource?)null)));
    }

    [Fact]
    public void SafeLabel_of_a_healthy_metadata_source_is_its_Name()
    {
        Assert.Equal("beta", PluginDiagnostics.SafeLabel(new FakeMetadataSource("beta")));
    }

    [Fact]
    public void SafeLabel_of_a_metadata_source_whose_Name_throws_falls_back_to_its_type_name()
    {
        var label = PluginDiagnostics.SafeLabel(new ThrowingNameMetadataSource());
        Assert.False(string.IsNullOrEmpty(label));
        Assert.DoesNotContain("boom", label);
    }

    [Fact]
    public void SafeLabel_of_null_metadata_source_does_not_throw()
    {
        var label = Record.Exception(() => PluginDiagnostics.SafeLabel((IMetadataSource?)null));
        Assert.Null(label);
    }

    [Fact]
    public void SafeLabel_of_null_metadata_source_returns_a_placeholder()
    {
        Assert.False(string.IsNullOrEmpty(PluginDiagnostics.SafeLabel((IMetadataSource?)null)));
    }
}

file sealed class FakeMetadataSource(string name) : IMetadataSource
{
    public string Name => name;
    public IReadOnlyList<string> SupportedMediaTypes { get; } = [];
    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>([]);
}

file sealed class ThrowingNameMetadataSource : IMetadataSource
{
    public string Name => throw new InvalidOperationException("name boom");
    public IReadOnlyList<string> SupportedMediaTypes { get; } = [];
    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>([]);
}
