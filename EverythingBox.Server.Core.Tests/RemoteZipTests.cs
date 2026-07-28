using System.IO.Compression;
using System.Text;
using EverythingBox.Server.Core.Scraping;

namespace EverythingBox.Server.Core.Tests;

/// <summary>
/// RemoteZip's whole point is reading a zip's central directory through ranged reads so
/// one member can be pulled without fetching the archive. ByteArrayRangeSource lets that
/// be exercised against a real zip with no HTTP and no timing.
/// </summary>
public class RemoteZipTests
{
    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    private static Task<RemoteZip> OpenAsync(byte[] zip)
        => RemoteZip.OpenAsync(new ByteArrayRangeSource(zip), CancellationToken.None);

    private static string NonCompressiblePayload(int length)
    {
        var rng = new Random(12345);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('a' + rng.Next(26));
        return new string(chars);
    }

    [Fact]
    public async Task Lists_every_entry_from_the_central_directory()
    {
        var zip = await OpenAsync(BuildZip(("a.txt", "alpha"), ("dir/b.txt", "beta")));

        var names = zip.Entries.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["a.txt", "dir/b.txt"], names);
    }

    [Fact]
    public async Task Extracts_one_member_without_touching_the_others()
    {
        var zip = await OpenAsync(BuildZip(("a.txt", "alpha"), ("b.txt", "beta")));

        var entry = zip.Find("b.txt");
        Assert.NotNull(entry);

        var bytes = await zip.ExtractBytesAsync(entry!, CancellationToken.None);
        Assert.Equal("beta", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Find_is_null_for_a_name_that_is_not_present()
    {
        var zip = await OpenAsync(BuildZip(("a.txt", "alpha")));
        Assert.Null(zip.Find("nope.txt"));
    }

    [Fact]
    public async Task Reads_a_member_stored_without_compression()
    {
        // Method 0 (stored) takes a different path through the extractor than deflate.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("stored.bin", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes("uncompressed payload");
            stream.Write(bytes, 0, bytes.Length);
        }

        var zip = await OpenAsync(buffer.ToArray());
        var found = zip.Find("stored.bin");
        Assert.NotNull(found);

        var extracted = await zip.ExtractBytesAsync(found!, CancellationToken.None);
        Assert.Equal("uncompressed payload", Encoding.UTF8.GetString(extracted));
    }

    [Fact]
    public async Task Refuses_something_that_is_not_a_zip()
    {
        var garbage = Encoding.UTF8.GetBytes(new string('x', 512));
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(garbage));
    }

    [Fact]
    public async Task Only_reads_the_ranges_it_needs()
    {
        // The point of the class: opening a large archive must not read all of it. The
        // "big" member has to actually stay big after compression — a repeated character
        // collapses under deflate to a few hundred bytes, which would make the archive
        // smaller than RemoteZip's own tail-prefetch window and defeat the test — so this
        // uses pseudo-random (seeded, for determinism) content instead.
        var zip = BuildZip(("small.txt", "x"), ("big.bin", NonCompressiblePayload(400_000)));
        var counting = new CountingRangeSource(new ByteArrayRangeSource(zip));

        var remote = await RemoteZip.OpenAsync(counting, CancellationToken.None);
        var entry = remote.Find("small.txt");
        Assert.NotNull(entry);
        await remote.ExtractBytesAsync(entry!, CancellationToken.None);

        Assert.True(counting.BytesRead < zip.Length / 2,
            $"Opening and extracting one small member read {counting.BytesRead} of {zip.Length} bytes — " +
            "RemoteZip is fetching far more than it needs.");
    }

    private sealed class CountingRangeSource(IRangeSource inner) : IRangeSource
    {
        public long BytesRead { get; private set; }
        public long Length => inner.Length;

        public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken)
        {
            var bytes = await inner.ReadAsync(offset, count, cancellationToken);
            BytesRead += bytes.Length;
            return bytes;
        }

        public IRangeSource Slice(long offset, long length) => inner.Slice(offset, length);
    }
}
