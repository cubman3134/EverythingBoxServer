# Optional post-download content verification (EBS#20)

**Status:** approved 2026-08-10, ready for planning.

## Goal

Let a caller assert the **expected checksum** of what the self-download path fetched, so a
corrupt or wrong file is **rejected before it is published/served**. Carry the expectation on
`TorrentResult`; verify each downloaded file against it before publish; skip (never serve) a
mismatch. Generic engine capability — names no content source, and does real work only when a
caller supplies an expectation.

## Why this is needed

Nothing verifies a self-downloaded file today. `ReleaseStreamResolver.TryFallbackDownloadAsync`
takes the downloader's local paths and hands each straight to `PublishAsync`, which just *moves*
the file into the served cache (`EverythingBox.Server/Sources/ReleaseStreamResolver.cs:282-288`,
`:382-395`). Every "hash" in the codebase today is a cache key or a BitTorrent info-hash — none
is content verification. A caller that already knows the expected hash of the content it asked
for (e.g. from a known-good checksum database) has no way to have the engine reject a bad fetch.

## Approach — data-driven, mirroring EBS#19

No new `IVerifier` interface: hash-compare is universal, so a pluggable verifier would be
over-build. The expectation rides on `TorrentResult` (the source→host channel, exactly like
`WantedMembers`), and the resolver honors it inline via a small pure helper.

### 1. Contract (`EverythingBox.Server.Abstractions`, additive, API 1.11 → 1.12)

New file `EverythingBox.Server.Abstractions/Results/MemberChecksum.cs`:
```csharp
namespace EverythingBox.Server.Abstractions;

/// <summary>Checksum algorithms the engine can verify a downloaded file against.
/// Built-in to .NET (no external dependency).</summary>
public enum ChecksumAlgorithm
{
    Md5,
    Sha1,
    Sha256,
}

/// <summary>
/// A caller-supplied expectation for one downloaded member: the member (a full in-torrent path
/// or a bare filename, matched the way <c>WantedMembers</c> is) must hash to <see cref="Hex"/>
/// under <see cref="Algorithm"/>. Used only by the self-download verification path.
/// </summary>
/// <param name="Member">Full in-torrent member path or bare filename.</param>
/// <param name="Algorithm">Which built-in hash to compute.</param>
/// <param name="Hex">Expected hash as a hex string (case-insensitive).</param>
public sealed record MemberChecksum(string Member, ChecksumAlgorithm Algorithm, string Hex);
```

Add to `EverythingBox.Server.Abstractions/Results/TorrentResult.cs` (after `WantedMembers`):
```csharp
/// <summary>
/// Expected checksums for downloaded members. When a downloaded file matches an entry here
/// (by member path or filename), the self-download path verifies it and refuses to publish a
/// mismatch. Empty (the default) skips verification entirely. Only the self-download
/// (MonoTorrent) path consults this; debrid/direct paths are unaffected.
/// </summary>
public IReadOnlyList<MemberChecksum> ExpectedChecksums { get; init; } = [];
```

Additive, default empty → no behavior change, and **zero work** (no hashing) for any producer
that doesn't set it. **API bump 1.11 → 1.12.**

### 2. The pure, testable core: `ChecksumVerifier`

New file `EverythingBox.Server/Download/ChecksumVerifier.cs` (host; `EverythingBox.Server` has
`InternalsVisibleTo("EverythingBox.Server.Tests")`):
```csharp
using System.Security.Cryptography;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Download;

internal static class ChecksumVerifier
{
    /// <summary>Streams <paramref name="content"/> through <paramref name="algorithm"/> and
    /// returns the lowercase hex digest — bounded memory, never buffers the whole file.</summary>
    public static async Task<string> ComputeHexAsync(
        Stream content, ChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        using var hash = Create(algorithm);
        var digest = await hash.ComputeHashAsync(content, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>True iff <paramref name="content"/> hashes to <paramref name="expectedHex"/>
    /// under <paramref name="algorithm"/>, compared case-insensitively.</summary>
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
```

(`Convert.ToHexStringLower` is .NET 9. `HashAlgorithm.ComputeHashAsync` streams, so a multi-GB
file is hashed with bounded memory.)

### 3. Wiring in `ReleaseStreamResolver.TryFallbackDownloadAsync`

Change the publish loop (`:282-288`) to verify each file before publishing:
```csharp
        var streams = new List<SourceStream>(localPaths.Count);
        foreach (var path in localPaths)
        {
            if (!await VerifyAsync(release, path, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Downloaded file '{File}' for '{Title}' failed checksum verification; not publishing it.",
                    Path.GetFileName(path), release.Title);
                continue;
            }

            var built = await PublishAsync(files, release, request, path, cancellationToken).ConfigureAwait(false);
            if (built is not null)
                streams.Add(new SourceStream($"files/{built.ServedName}", built.ContentType));
        }
```

Add the `VerifyAsync` helper on the resolver:
```csharp
    /// <summary>
    /// True when <paramref name="localPath"/> is allowed to be published: either the release
    /// gave no expected checksum for it, or its content matches the expected one. A read error
    /// is treated as a failure (never publish something we couldn't verify when a checksum was
    /// demanded).
    /// </summary>
    private async Task<bool> VerifyAsync(TorrentResult release, string localPath, CancellationToken cancellationToken)
    {
        if (release.ExpectedChecksums.Count == 0)
            return true;

        var fileName = Path.GetFileName(localPath);
        var expected = release.ExpectedChecksums.FirstOrDefault(
            c => MemberMatches(c.Member, fileName));
        if (expected is null)
            return true; // no expectation for this file → publish as usual

        try
        {
            await using var stream = File.OpenRead(localPath);
            return await ChecksumVerifier.MatchesAsync(stream, expected.Algorithm, expected.Hex, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read '{File}' to verify its checksum.", fileName);
            return false;
        }
    }

    // A checksum entry matches a downloaded file by filename, case-insensitively. The entry's
    // Member may be a bare filename or a full member path; either way we compare its last path
    // segment to the downloaded file's name (MonoTorrent lays each member down under its own
    // filename, so the downloaded file's name IS the member's filename).
    private static bool MemberMatches(string member, string fileName)
    {
        var m = member.Replace('\\', '/');
        var last = m.LastIndexOf('/');
        var memberName = last >= 0 ? m[(last + 1)..] : m;
        return string.Equals(memberName, fileName, StringComparison.OrdinalIgnoreCase);
    }
```

### 4. Clean the working directory even when verification rejected everything

Today the working-directory cleanup (`:290-299`) runs only when `streams.Count > 0`. If
verification rejects *every* file, the rejected bytes would linger in `.downloads`. Change the
guard so the cleanup runs whenever something was downloaded:
```csharp
        if (localPaths.Count > 0)
            RemoveDownloadDirectory(DownloadDirectory(files, release, request));

        return streams.Count > 0 ? streams : null;
```
(Moving files that *were* published out of `.downloads` already happened in `PublishAsync`; this
removes whatever is left — unpublished/rejected files, engine scratch — in both the success and
the all-rejected case. Best-effort, as before.)

## Failure semantics

- A file whose content does not match its expected checksum is **never published** — it is
  skipped and logged. Verification only ever *removes* a file from the published set.
- If verification leaves **zero** publishable files, `TryFallbackDownloadAsync` returns `null`
  and the caller degrades to the caching notice — identical to a failed/empty download.
- A file with **no** matching `MemberChecksum` publishes exactly as today (no expectation, no
  verification, no cost).
- An unreadable file (when a checksum *was* demanded) fails closed (not published).

## Composition with EBS#19 and EBS#5

Order in the self-download path: members selected (EBS#19 `WantedMembers`) → summed size capped
before download (EBS#5 `maxTotalBytes`) → files downloaded → **verified (this change)** →
published. Each stage only narrows what is served; they compose with no special-casing.

## Algorithms (approved)

Built-in `MD5` / `SHA1` / `SHA256` only, via `System.Security.Cryptography` — **no new NuGet
dependency** on the engine. `CRC32` is out of scope (it needs `System.IO.Hashing`); a
caller can verify with SHA-1, which is universally available in the known-good checksum
databases this targets.

## Testing

- **`ChecksumVerifier` unit tests** (in a new `EverythingBox.Server.Tests/ChecksumVerifierTests.cs`,
  using the `internal` helper via the existing `InternalsVisibleTo`):
  - `ComputeHexAsync` returns the known digest of a fixed byte string for each of MD5, SHA1,
    SHA256 (hard-code the expected hex of, e.g., the ASCII bytes of `"abc"`).
  - `MatchesAsync` is true for the correct hex and false for a wrong hex.
  - `MatchesAsync` is case-insensitive (upper-case expected hex still matches) and tolerates
    surrounding whitespace in the expected value.
- **Contract/version:** `TorrentResult.ExpectedChecksums` defaults empty; `ServerApi`
  `VersionString` 1.11 → 1.12; both version-pin tests (`ServerApiContractTests`,
  `MetadataContractTests`) update to Minor 12, and `[InlineData(1, 11)]` is added to the
  earlier-minor compat theory.
- The live `TryFallbackDownloadAsync`/`VerifyAsync` wiring is not unit-tested for the same
  reason the rest of that swarm-facing path isn't (it needs a real download); the verification
  *decision* is fully covered by `ChecksumVerifier` tests, and `VerifyAsync`'s no-expectation
  short-circuit is a plain guard.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- Additive contract change: a new enum, a new record, and a new `TorrentResult` field defaulting
  to `[]` → **no behavior change** and **no hashing cost** for any producer that doesn't set it.
  Single API minor bump 1.11 → 1.12.
- **No new dependency** — built-in crypto only.
- **Cleanliness:** fully generic — no content-source name in code, comments, paths, tests, or
  the commit message. `RepositoryCleanlinessTests` must stay green.
- `ITorrentDownloader.DownloadAsync` is **unchanged** (the expectation rides on `TorrentResult`).
- Fails safe: verification can only withhold a file, never serve an unverified one.

## Out of scope

- Any producer that **sets** `ExpectedChecksums` (a source that has known-good hashes for what it
  resolves) — downstream, plugin-side work with its own issue.
- `CRC32` and any non-built-in algorithm (would add a dependency).
- Resume/concurrency (EBS#21); explicit member selection (EBS#19, done).
- A pluggable `IVerifier` abstraction (YAGNI — the expectation is pure data + a built-in hash).

## Done when

- A `TorrentResult` carrying an `ExpectedChecksums` entry for a downloaded file causes that file
  to be published only if its content matches; a mismatch is skipped and logged; an all-mismatch
  download degrades to the caching notice; a file with no expectation publishes unchanged.
- `ChecksumVerifier` is unit-tested across MD5/SHA1/SHA256 with case-insensitive hex; the working
  directory is cleaned even when everything was rejected.
- API is 1.12; both engine test projects green including `RepositoryCleanlinessTests` and the
  version-pin/compat tests; `DownloadAsync` signature unchanged; no new package reference.
  Verified in Release.
