using System.Security.Cryptography;
using System.Text;
using EverythingBox.Server.Core.Torrents;

namespace EverythingBox.Server.Core.Tests;

public class TorrentInfoTests
{
    // A valid info dictionary (order doesn't matter for hashing — we hash the bytes).
    private static readonly byte[] Info =
        Encoding.UTF8.GetBytes("d6:lengthi1024e4:name8:test.iso12:piece lengthi16384e6:pieces20:01234567890123456789e");

    private static byte[] BuildTorrent(byte[] info)
    {
        // d 8:announce <url> 4:info <info> e
        var announce = Encoding.UTF8.GetBytes("8:announce18:udp://tracker:6969");
        return [(byte)'d', .. announce, .. Encoding.UTF8.GetBytes("4:info"), .. info, (byte)'e'];
    }

    [Fact]
    public void ReadsInfoHashFromTorrentBytes()
    {
        var expected = Convert.ToHexString(SHA1.HashData(Info)).ToLowerInvariant();

        Assert.True(TorrentInfo.TryReadInfoHash(BuildTorrent(Info), out var hash));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void ReturnsFalseWhenNoInfoDictionary()
    {
        var bytes = Encoding.UTF8.GetBytes("d8:announce18:udp://tracker:6969e");
        Assert.False(TorrentInfo.TryReadInfoHash(bytes, out _));
    }

    [Fact]
    public void ReturnsFalseForGarbage()
        => Assert.False(TorrentInfo.TryReadInfoHash(Encoding.UTF8.GetBytes("not a torrent"), out _));
}
