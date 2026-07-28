using System.Buffers.Binary;
using System.IO.Compression;

namespace EverythingBox.Server.Core.Scraping;

/// <summary>
/// A read-only ZIP reader that works over HTTP range requests, so a single member
/// can be extracted without downloading the whole archive — it fetches only the
/// central directory and the wanted member's bytes. Built against a remote host
/// serving zip files over HTTP range requests, but works against any
/// <see cref="IRangeSource"/>. Nested zips (a zip stored inside another) are opened
/// recursively, which lets a member "too deep" for a server's one-level extraction
/// still be pulled on its own. Supports stored and deflate entries and ZIP64; uses
/// only the BCL.
/// </summary>
public sealed class RemoteZip
{
    private readonly IRangeSource _source;

    private RemoteZip(IRangeSource source, IReadOnlyList<ZipEntry> entries)
    {
        _source = source;
        Entries = entries;
    }

    public IReadOnlyList<ZipEntry> Entries { get; }

    public ZipEntry? Find(string name)
        => Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static async Task<RemoteZip> OpenAsync(IRangeSource source, CancellationToken cancellationToken = default)
    {
        var total = source.Length;
        if (total < 22)
            throw new InvalidDataException("not a zip (too small)");

        // End of Central Directory: scan the tail for the signature (the trailing
        // comment is variable-length, up to 64 KB).
        var tailLen = (int)Math.Min(65557, total);
        var tail = await source.ReadAsync(total - tailLen, tailLen, cancellationToken).ConfigureAwait(false);
        var eocd = LastIndexOf(tail, [0x50, 0x4B, 0x05, 0x06]);
        if (eocd < 0)
            throw new InvalidDataException("end-of-central-directory record not found");

        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        long entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));

        // ZIP64: real values live in the ZIP64 EOCD when any field is maxed out.
        if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF || entryCount == 0xFFFF)
        {
            var locator = LastIndexOf(tail, [0x50, 0x4B, 0x06, 0x07]);
            if (locator >= 0)
            {
                var z64Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
                var z64 = await source.ReadAsync(z64Offset, 56, cancellationToken).ConfigureAwait(false);
                if (z64.Length >= 56 && z64[0] == 0x50 && z64[1] == 0x4B && z64[2] == 0x06 && z64[3] == 0x06)
                {
                    entryCount = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(32));
                    cdSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(40));
                    cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(z64.AsSpan(48));
                }
            }
        }

        var cd = await source.ReadAsync(cdOffset, (int)cdSize, cancellationToken).ConfigureAwait(false);
        return new RemoteZip(source, ParseCentralDirectory(cd));
    }

    /// <summary>Extract a single entry's bytes (decompressing deflate as needed).</summary>
    public async Task<byte[]> ExtractBytesAsync(ZipEntry entry, CancellationToken cancellationToken = default)
    {
        // The local header repeats name/extra lengths, which can differ from the
        // central directory's — read it to find where the data actually starts.
        var header = await _source.ReadAsync(entry.LocalHeaderOffset, 30, cancellationToken).ConfigureAwait(false);
        if (header.Length < 30 || header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x03 || header[3] != 0x04)
            throw new InvalidDataException("local file header not found");

        int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var dataOffset = entry.LocalHeaderOffset + 30 + nameLen + extraLen;

        var raw = await _source.ReadAsync(dataOffset, (int)entry.CompressedSize, cancellationToken).ConfigureAwait(false);
        if (entry.Method == 0)
            return raw;
        if (entry.Method != 8)
            throw new NotSupportedException($"unsupported zip compression method {entry.Method}");

        using var deflate = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var output = new MemoryStream(checked((int)entry.UncompressedSize));
        await deflate.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    /// <summary>Open a zip entry that is itself a zip, recursively.</summary>
    public async Task<RemoteZip> OpenNestedAsync(ZipEntry entry, CancellationToken cancellationToken = default)
    {
        // A stored inner zip is verbatim bytes, so we can range into it in place
        // (downloading only what each read needs). A deflated one must be inflated
        // first, then read from memory.
        if (entry.Method == 0)
        {
            var header = await _source.ReadAsync(entry.LocalHeaderOffset, 30, cancellationToken).ConfigureAwait(false);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
            var dataOffset = entry.LocalHeaderOffset + 30 + nameLen + extraLen;
            return await OpenAsync(_source.Slice(dataOffset, entry.CompressedSize), cancellationToken).ConfigureAwait(false);
        }

        var bytes = await ExtractBytesAsync(entry, cancellationToken).ConfigureAwait(false);
        return await OpenAsync(new ByteArrayRangeSource(bytes), cancellationToken).ConfigureAwait(false);
    }

    private static List<ZipEntry> ParseCentralDirectory(byte[] cd)
    {
        var entries = new List<ZipEntry>();
        var i = 0;
        while (i + 46 <= cd.Length && cd[i] == 0x50 && cd[i + 1] == 0x4B && cd[i + 2] == 0x01 && cd[i + 3] == 0x02)
        {
            var method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(i + 10));
            long compSize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(i + 20));
            long uncompSize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(i + 24));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(i + 28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(i + 30));
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(i + 32));
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(i + 42));
            var name = System.Text.Encoding.UTF8.GetString(cd, i + 46, nameLen);

            // ZIP64 extra field (0x0001) supplies any 0xFFFFFFFF value above.
            var extraStart = i + 46 + nameLen;
            (uncompSize, compSize, localOffset) = ApplyZip64(cd, extraStart, extraLen, uncompSize, compSize, localOffset);

            if (!name.EndsWith('/'))
                entries.Add(new ZipEntry(name, method, compSize, uncompSize, localOffset));
            i = extraStart + extraLen + commentLen;
        }
        return entries;
    }

    private static (long Uncomp, long Comp, long Local) ApplyZip64(
        byte[] cd, int start, int length, long uncomp, long comp, long local)
    {
        var p = start;
        var end = start + length;
        while (p + 4 <= end && p + 4 <= cd.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 2));
            var field = p + 4;
            if (id == 0x0001)
            {
                if (uncomp == 0xFFFFFFFF) { uncomp = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(field)); field += 8; }
                if (comp == 0xFFFFFFFF) { comp = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(field)); field += 8; }
                if (local == 0xFFFFFFFF) { local = (long)BinaryPrimitives.ReadUInt64LittleEndian(cd.AsSpan(field)); }
            }
            p += 4 + size;
        }
        return (uncomp, comp, local);
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok)
                return i;
        }
        return -1;
    }
}

/// <summary>A zip central-directory entry.</summary>
public sealed record ZipEntry(string Name, ushort Method, long CompressedSize, long UncompressedSize, long LocalHeaderOffset);

/// <summary>Random-access byte source for <see cref="RemoteZip"/>.</summary>
public interface IRangeSource
{
    long Length { get; }
    Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default);
    IRangeSource Slice(long offset, long length);
}

/// <summary>An <see cref="IRangeSource"/> backed by HTTP range requests.</summary>
public sealed class HttpRangeSource : IRangeSource
{
    // Size of the trailing chunk fetched up front. A zip's end-of-central-directory
    // and (for most large archives) the whole central directory live here, so opening
    // the zip costs a single request instead of three.
    private const int TailPrefetch = 256 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly long _baseOffset;
    private readonly byte[]? _tail;
    private readonly long _tailOffset;

    private HttpRangeSource(HttpClient http, Uri url, long baseOffset, long length, byte[]? tail = null, long tailOffset = 0)
    {
        _http = http;
        _url = url;
        _baseOffset = baseOffset;
        Length = length;
        _tail = tail;
        _tailOffset = tailOffset;
    }

    public long Length { get; }

    /// <summary>
    /// Resolve the final URL (after redirects) and total length, prefetching the file's
    /// tail in the same request (a suffix range) so the directory reads are served from
    /// memory.
    /// </summary>
    public static async Task<HttpRangeSource> CreateAsync(HttpClient http, Uri url, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var suffix = new System.Net.Http.Headers.RangeHeaderValue();
        suffix.Ranges.Add(new System.Net.Http.Headers.RangeItemHeaderValue(null, TailPrefetch)); // bytes=-N (last N)
        request.Headers.Range = suffix;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var final = response.RequestMessage?.RequestUri ?? url;

        // 206 → Content-Range gives the total and where this chunk starts; 200 → the
        // server ignored the range and sent the whole file.
        if (response.Content.Headers.ContentRange is { Length: { } total } range)
            return new HttpRangeSource(http, final, 0, total, body, range.From ?? total - body.Length);

        var length = response.Content.Headers.ContentLength ?? body.Length;
        return new HttpRangeSource(http, final, 0, length, body, 0);
    }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default)
    {
        var from = _baseOffset + offset;

        // Serve from the prefetched tail when the range falls inside it.
        if (_tail is not null && from >= _tailOffset && from + count <= _tailOffset + _tail.Length)
        {
            var start = (int)(from - _tailOffset);
            return _tail[start..(start + count)];
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, from + count - 1);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    // Slices share the parent's prefetched tail (offsets re-based), so a stored inner
    // zip whose directory sits in the tail also opens without extra requests.
    public IRangeSource Slice(long offset, long length)
        => new HttpRangeSource(_http, _url, _baseOffset + offset, length, _tail, _tailOffset);
}

/// <summary>An <see cref="IRangeSource"/> over an in-memory byte array.</summary>
public sealed class ByteArrayRangeSource(byte[] data, long offset = 0, long? length = null) : IRangeSource
{
    public long Length { get; } = length ?? data.Length - offset;

    public Task<byte[]> ReadAsync(long readOffset, int count, CancellationToken cancellationToken = default)
    {
        var start = (int)(offset + readOffset);
        var n = Math.Min(count, data.Length - start);
        return Task.FromResult(data[start..(start + n)]);
    }

    public IRangeSource Slice(long sliceOffset, long sliceLength) => new ByteArrayRangeSource(data, offset + sliceOffset, sliceLength);
}
