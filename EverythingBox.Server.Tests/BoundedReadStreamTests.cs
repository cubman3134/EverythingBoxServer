using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

public class BoundedReadStreamTests
{
    private sealed class DisposeProbe(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task Yields_exactly_the_bounded_slice()
    {
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
        var inner = new MemoryStream(data);
        inner.Seek(10, SeekOrigin.Begin);

        using var bounded = new BoundedReadStream(inner, 20);
        using var sink = new MemoryStream();
        await bounded.CopyToAsync(sink);

        var got = sink.ToArray();
        Assert.Equal(20, got.Length);
        Assert.Equal(Enumerable.Range(10, 20).Select(i => (byte)i), got); // bytes [10,30)
    }

    [Fact]
    public async Task Disposing_disposes_the_inner_stream()
    {
        var probe = new DisposeProbe(new byte[50]);
        var bounded = new BoundedReadStream(probe, 10);
        await bounded.DisposeAsync();
        Assert.True(probe.Disposed);
    }

    [Fact]
    public void Reports_the_bounded_length_and_is_read_only()
    {
        using var bounded = new BoundedReadStream(new MemoryStream(new byte[50]), 10);
        Assert.Equal(10, bounded.Length);
        Assert.True(bounded.CanRead);
        Assert.False(bounded.CanWrite);
        Assert.False(bounded.CanSeek);
    }
}
