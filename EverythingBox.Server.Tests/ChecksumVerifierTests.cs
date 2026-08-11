using System.Text;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Download;

namespace EverythingBox.Server.Tests;

public class ChecksumVerifierTests
{
    private static MemoryStream Bytes(string s) => new(Encoding.ASCII.GetBytes(s));

    // Known digests of ASCII "abc".
    [Theory]
    [InlineData(ChecksumAlgorithm.Md5, "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData(ChecksumAlgorithm.Sha1, "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData(ChecksumAlgorithm.Sha256, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public async Task ComputeHexAsync_returns_the_known_digest(ChecksumAlgorithm algorithm, string expected)
    {
        var hex = await ChecksumVerifier.ComputeHexAsync(Bytes("abc"), algorithm);
        Assert.Equal(expected, hex);
    }

    [Fact]
    public async Task MatchesAsync_is_true_for_the_correct_hash()
    {
        Assert.True(await ChecksumVerifier.MatchesAsync(
            Bytes("abc"), ChecksumAlgorithm.Sha1, "a9993e364706816aba3e25717850c26c9cd0d89d"));
    }

    [Fact]
    public async Task MatchesAsync_is_false_for_a_wrong_hash()
    {
        Assert.False(await ChecksumVerifier.MatchesAsync(
            Bytes("abc"), ChecksumAlgorithm.Sha1, "0000000000000000000000000000000000000000"));
    }

    [Fact]
    public async Task MatchesAsync_ignores_case_and_surrounding_whitespace_in_the_expected_hex()
    {
        Assert.True(await ChecksumVerifier.MatchesAsync(
            Bytes("abc"), ChecksumAlgorithm.Sha1, "  A9993E364706816ABA3E25717850C26C9CD0D89D  "));
    }
}
