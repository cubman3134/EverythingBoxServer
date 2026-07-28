using System.Security.Cryptography;

namespace EverythingBox.Server.Core.Torrents;

/// <summary>
/// Minimal bencode reader that extracts the v1 BitTorrent info hash from raw
/// <c>.torrent</c> bytes — the SHA-1 of the bencoded <c>info</c> dictionary — using
/// only the BCL. Lets us turn a <c>.torrent</c> file into a magnet without a
/// BitTorrent library.
/// </summary>
public static class TorrentInfo
{
    public static bool TryReadInfoHash(ReadOnlySpan<byte> data, out string infoHashHex)
    {
        infoHashHex = string.Empty;
        var pos = 0;
        if (pos >= data.Length || data[pos] != (byte)'d')
            return false;
        pos++;

        while (pos < data.Length && data[pos] != (byte)'e')
        {
            if (!TryReadString(data, ref pos, out var keyStart, out var keyLength))
                return false;

            var valueStart = pos;
            if (!TrySkipValue(data, ref pos))
                return false;

            if (keyLength == 4 && data.Slice(keyStart, keyLength).SequenceEqual("info"u8))
            {
                infoHashHex = Convert.ToHexString(SHA1.HashData(data[valueStart..pos])).ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int pos, out int start, out int length)
    {
        start = 0;
        length = 0;
        var n = 0;
        var any = false;
        while (pos < data.Length && data[pos] is >= (byte)'0' and <= (byte)'9')
        {
            n = (n * 10) + (data[pos] - (byte)'0');
            pos++;
            any = true;
        }

        if (!any || pos >= data.Length || data[pos] != (byte)':')
            return false;
        pos++;
        if (n < 0 || pos + n > data.Length)
            return false;

        start = pos;
        length = n;
        pos += n;
        return true;
    }

    private static bool TrySkipValue(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos >= data.Length)
            return false;

        switch (data[pos])
        {
            case (byte)'i':
                pos++;
                while (pos < data.Length && data[pos] != (byte)'e') pos++;
                if (pos >= data.Length) return false;
                pos++;
                return true;

            case (byte)'l':
                pos++;
                while (pos < data.Length && data[pos] != (byte)'e')
                    if (!TrySkipValue(data, ref pos)) return false;
                if (pos >= data.Length) return false;
                pos++;
                return true;

            case (byte)'d':
                pos++;
                while (pos < data.Length && data[pos] != (byte)'e')
                {
                    if (!TryReadString(data, ref pos, out _, out _)) return false;
                    if (!TrySkipValue(data, ref pos)) return false;
                }
                if (pos >= data.Length) return false;
                pos++;
                return true;

            default:
                return data[pos] is >= (byte)'0' and <= (byte)'9' && TryReadString(data, ref pos, out _, out _);
        }
    }
}
