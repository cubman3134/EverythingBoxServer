using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// A source implementing only the REQUIRED members — proves the optional ones
/// have working defaults, so a simple plugin never writes them.
/// </summary>
file sealed class MinimalSource : IMediaSource
{
    public string Key => "minimal";

    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } =
        [new CatalogDescriptor("all", "Everything", "movie")];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("Everything", [new CatalogItem("x", "X", "sub", "movie")]));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("Everything"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(new SourceStream("https://example.test/a.mkv", "video/x-matroska"));
}

/// <summary>Throws from DisposeAsync — stands in for a network stream on a broken
/// connection, which is the realistic case F9 guards against.</summary>
file sealed class ThrowingDisposeStream : MemoryStream
{
    public override ValueTask DisposeAsync() => throw new InvalidOperationException("Body.DisposeAsync boom");
}

file sealed class RecordingDisposable : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

public class MediaSourceContractTests
{
    [Fact]
    public void MediaTypes_defaults_to_empty()
    {
        IMediaSource source = new MinimalSource();
        Assert.Empty(source.MediaTypes);
    }

    [Fact]
    public async Task OpenAsync_defaults_to_null()
    {
        IMediaSource source = new MinimalSource();
        Assert.Null(await source.OpenAsync("x", null, CancellationToken.None));
    }

    [Fact]
    public async Task WarmUpAsync_defaults_to_not_applicable()
    {
        IMediaSource source = new MinimalSource();
        var result = await source.WarmUpAsync(CancellationToken.None);
        Assert.Equal(WarmUpStatus.NotApplicable, result.Status);
    }

    [Fact]
    public void SourceStream_from_notice_carries_no_url()
    {
        var s = SourceStream.FromNotice("caching, retry shortly");
        Assert.Equal("", s.Url);
        Assert.Equal("caching, retry shortly", s.Notice);
    }

    // F9: DisposeAsync used to dispose Body then Owner with no try/finally. Body is a
    // network stream that can plausibly throw on dispose (a broken connection); without
    // try/finally that throw skips disposing Owner — typically an HttpResponseMessage
    // holding the underlying connection — and it leaks.
    [Fact]
    public async Task DisposeAsync_still_disposes_Owner_when_Body_DisposeAsync_throws()
    {
        var owner = new RecordingDisposable();
        var proxy = new ProxyResponse(new ThrowingDisposeStream(), "application/octet-stream") { Owner = owner };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await proxy.DisposeAsync());

        Assert.True(owner.Disposed, "Owner must still be disposed even though Body.DisposeAsync threw.");
    }
}
