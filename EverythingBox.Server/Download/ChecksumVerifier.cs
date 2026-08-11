using System.Security.Cryptography;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Download;

/// <summary>
/// Verifies a downloaded file's content against a caller-supplied expected checksum. Streams the
/// file through the algorithm so a large file is hashed with bounded memory. Built-in algorithms
/// only — no external dependency.
/// </summary>
internal static class ChecksumVerifier
{
    /// <summary>The lowercase hex digest of <paramref name="content"/> under <paramref name="algorithm"/>.</summary>
    public static async Task<string> ComputeHexAsync(
        Stream content, ChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        using var hash = Create(algorithm);
        var digest = await hash.ComputeHashAsync(content, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>True iff <paramref name="content"/> hashes to <paramref name="expectedHex"/>
    /// under <paramref name="algorithm"/>, compared case-insensitively (whitespace trimmed).</summary>
    public static async Task<bool> MatchesAsync(
        Stream content, ChecksumAlgorithm algorithm, string expectedHex, CancellationToken cancellationToken = default)
    {
        var actual = await ComputeHexAsync(content, algorithm, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static HashAlgorithm Create(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Md5 => MD5.Create(),
        ChecksumAlgorithm.Sha1 => SHA1.Create(),
        ChecksumAlgorithm.Sha256 => SHA256.Create(),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "unsupported checksum algorithm"),
    };
}
