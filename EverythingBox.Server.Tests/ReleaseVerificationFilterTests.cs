using System.Text;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Sources;

namespace EverythingBox.Server.Tests;

/// <summary>
/// Pins the keep/reject decision the self-download verification path makes, driven over real
/// temp files without constructing the whole host. This is the unit that moved out of the
/// per-request publish loop and into the memoized download step: a file whose content matches
/// its expected checksum is kept, a mismatch (or unreadable file) is dropped, and a file with
/// no expectation at all is kept untouched.
/// </summary>
public class ReleaseVerificationFilterTests : IDisposable
{
    private readonly string _dir;

    public ReleaseVerificationFilterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ebs-verify-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(content));
        return path;
    }

    // SHA-1 of ASCII "abc"; the b.bin expectation is deliberately wrong.
    private const string Sha1OfAbc = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string WrongSha1 = "0000000000000000000000000000000000000000";

    [Fact]
    public async Task KeepVerifiedPaths_keeps_only_the_file_whose_content_matches_its_checksum()
    {
        var good = Write("a.bin", "abc");
        var bad = Write("b.bin", "xyz");

        var expected = new List<MemberChecksum>
        {
            new("a.bin", ChecksumAlgorithm.Sha1, Sha1OfAbc),
            new("b.bin", ChecksumAlgorithm.Sha1, WrongSha1),
        };

        var kept = await ReleaseStreamResolver.KeepVerifiedPaths(expected, new[] { good, bad }, CancellationToken.None);

        Assert.Equal(new[] { good }, kept);
    }

    [Fact]
    public async Task KeepVerifiedPaths_returns_empty_when_every_file_fails_its_checksum()
    {
        var a = Write("a.bin", "abc");
        var b = Write("b.bin", "xyz");

        var expected = new List<MemberChecksum>
        {
            new("a.bin", ChecksumAlgorithm.Sha1, WrongSha1),
            new("b.bin", ChecksumAlgorithm.Sha1, WrongSha1),
        };

        var kept = await ReleaseStreamResolver.KeepVerifiedPaths(expected, new[] { a, b }, CancellationToken.None);

        Assert.Empty(kept);
    }

    [Fact]
    public async Task KeepVerifiedPaths_keeps_every_path_unchanged_when_there_are_no_expectations()
    {
        var a = Write("a.bin", "abc");
        var b = Write("b.bin", "xyz");
        var paths = new[] { a, b };

        var kept = await ReleaseStreamResolver.KeepVerifiedPaths([], paths, CancellationToken.None);

        Assert.Same(paths, kept);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
