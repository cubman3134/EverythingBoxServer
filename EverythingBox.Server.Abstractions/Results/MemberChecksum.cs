namespace EverythingBox.Server.Abstractions;

/// <summary>Checksum algorithms the engine can verify a downloaded file against. All built-in
/// to .NET (no external dependency).</summary>
public enum ChecksumAlgorithm
{
    Md5,
    Sha1,
    Sha256,
}

/// <summary>
/// A caller-supplied expectation for one downloaded member: the member (a full in-torrent path
/// or a bare filename, matched by filename) must hash to <see cref="Hex"/> under
/// <see cref="Algorithm"/>. Used only by the self-download verification path.
/// </summary>
/// <param name="Member">Full in-torrent member path or bare filename.</param>
/// <param name="Algorithm">Which built-in hash to compute.</param>
/// <param name="Hex">Expected hash as a hex string (case-insensitive).</param>
public sealed record MemberChecksum(string Member, ChecksumAlgorithm Algorithm, string Hex);
