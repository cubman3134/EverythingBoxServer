using System.Net;
using System.Security.Cryptography;
using System.Text;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Debrid;

namespace EverythingBox.Server.Core.Tests;

public class MagnetResolverTests
{
    private static readonly byte[] Info =
        Encoding.UTF8.GetBytes("d6:lengthi1024e4:name8:test.iso12:piece lengthi16384e6:pieces20:01234567890123456789e");

    private static byte[] TorrentBytes()
        => [(byte)'d', .. Encoding.UTF8.GetBytes("8:announce18:udp://tracker:6969"), .. Encoding.UTF8.GetBytes("4:info"), .. Info, (byte)'e'];

    [Fact]
    public async Task UsesMagnetWhenPresent()
    {
        var torrent = new TorrentResult { Title = "X", ProviderName = "p", MagnetUri = new Uri("magnet:?xt=urn:btih:ABC") };
        Assert.Equal("magnet:?xt=urn:btih:ABC", await MagnetResolver.ResolveAsync(new HttpClient(new ThrowingHandler()), torrent));
    }

    [Fact]
    public async Task BuildsMagnetFromInfoHash()
    {
        var torrent = new TorrentResult { Title = "X", ProviderName = "p", InfoHash = "DEAD" };
        Assert.Equal("magnet:?xt=urn:btih:DEAD", await MagnetResolver.ResolveAsync(new HttpClient(new ThrowingHandler()), torrent));
    }

    [Fact]
    public async Task FetchesAndReadsInfoHashFromTorrentFile()
    {
        var expected = Convert.ToHexString(SHA1.HashData(Info)).ToLowerInvariant();
        var http = new HttpClient(new BytesHandler(TorrentBytes()));
        var torrent = new TorrentResult { Title = "Cool Thing", ProviderName = "p", DownloadUrl = new Uri("http://files/x.torrent") };

        var magnet = await MagnetResolver.ResolveAsync(http, torrent);

        Assert.StartsWith($"magnet:?xt=urn:btih:{expected}", magnet);
        Assert.Contains("dn=Cool%20Thing", magnet);
    }

    [Fact]
    public async Task ReturnsNullWhenNothingUsable()
    {
        var torrent = new TorrentResult { Title = "X", ProviderName = "p", DownloadUrl = new Uri("http://files/page.html") };
        Assert.Null(await MagnetResolver.ResolveAsync(new HttpClient(new ThrowingHandler()), torrent));
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("should not be called");
    }
}
