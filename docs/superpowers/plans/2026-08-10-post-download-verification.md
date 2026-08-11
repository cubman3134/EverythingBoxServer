# Post-Download Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a caller assert the expected checksum of a self-downloaded file (via `TorrentResult.ExpectedChecksums`) so the engine refuses to publish a mismatch, verified before publish with built-in hashes only.

**Architecture:** A pure `ChecksumVerifier` (streams a file through MD5/SHA1/SHA256) plus a `ChecksumAlgorithm` enum and `MemberChecksum` record in Abstractions. `TorrentResult` gains an additive `ExpectedChecksums` list (the source→host channel — `DownloadAsync` unchanged). `ReleaseStreamResolver` verifies each downloaded file before publishing it and skips mismatches; when everything is rejected it degrades to the caching notice and still cleans the working dir.

**Tech Stack:** .NET 9 / C#, xUnit. `System.Security.Cryptography` (built-in — NO new package). `EverythingBox.Server.Abstractions` (contract), `EverythingBox.Server` (host, has `InternalsVisibleTo("EverythingBox.Server.Tests")`). Tests in `EverythingBox.Server.Tests`; version-pin tests in `EverythingBox.Server.Core.Tests`.

## Global Constraints

- **PUBLIC repo — no content-source name anywhere** (code, comments, paths, test fixtures, commit message). `RepositoryCleanlinessTests` scans contents + paths + commit message + full history. Keep everything generic.
- **Single additive API bump 1.11 → 1.12.** New enum + record + a `TorrentResult` field defaulting `[]`; verification does zero work (no hashing) when the field is empty → no behavior change for existing producers.
- **NO new NuGet/package dependency** — built-in `System.Security.Cryptography` only (MD5/SHA1/SHA256). No CRC32.
- `ITorrentDownloader.DownloadAsync` signature is **UNCHANGED** (the expectation rides on `TorrentResult`).
- Verification **fails safe**: it can only withhold a file, never serve an unverified one.
- Stage files by explicit path (never `git add -A`). No AI attribution in any commit.
- No test spawns a process, touches the network, or reads a real browser profile.
- Run tests per-project — this CLI rejects two projects in one `dotnet test` call (MSB1008).

---

### Task 1: `ChecksumAlgorithm` + `MemberChecksum` + the `ChecksumVerifier` helper

**Files:**
- Create: `EverythingBox.Server.Abstractions/Results/MemberChecksum.cs` (enum + record)
- Create: `EverythingBox.Server/Download/ChecksumVerifier.cs`
- Test: `EverythingBox.Server.Tests/ChecksumVerifierTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum ChecksumAlgorithm { Md5, Sha1, Sha256 }`; `sealed record MemberChecksum(string Member, ChecksumAlgorithm Algorithm, string Hex)` (both in namespace `EverythingBox.Server.Abstractions`); `internal static class ChecksumVerifier` with `Task<string> ComputeHexAsync(Stream, ChecksumAlgorithm, CancellationToken = default)` and `Task<bool> MatchesAsync(Stream, ChecksumAlgorithm, string expectedHex, CancellationToken = default)` (namespace `EverythingBox.Server.Download`).

- [ ] **Step 1: Write the failing verifier tests**

Create `EverythingBox.Server.Tests/ChecksumVerifierTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~ChecksumVerifier" -v minimal`
Expected: FAIL — build errors (`ChecksumAlgorithm` and `ChecksumVerifier` don't exist yet).

- [ ] **Step 3: Create the contract types**

Create `EverythingBox.Server.Abstractions/Results/MemberChecksum.cs`:

```csharp
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
```

- [ ] **Step 4: Create the verifier**

Create `EverythingBox.Server/Download/ChecksumVerifier.cs`:

```csharp
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
```

(If `Convert.ToHexStringLower` is unavailable on the toolchain, use `Convert.ToHexString(digest).ToLowerInvariant()` — same result.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~ChecksumVerifier" -v minimal`
Expected: PASS — all six cases green.

- [ ] **Step 6: Commit**

```bash
git add EverythingBox.Server.Abstractions/Results/MemberChecksum.cs EverythingBox.Server/Download/ChecksumVerifier.cs EverythingBox.Server.Tests/ChecksumVerifierTests.cs
git commit -m "feat: add a built-in checksum verifier and the MemberChecksum contract type"
```

---

### Task 2: `TorrentResult.ExpectedChecksums`, resolver verification, and API 1.12

**Files:**
- Modify: `EverythingBox.Server.Abstractions/Results/TorrentResult.cs` (add `ExpectedChecksums`)
- Modify: `EverythingBox.Server/Sources/ReleaseStreamResolver.cs` (verify before publish; clean-dir guard)
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` (`VersionString` 1.11 → 1.12)
- Modify: `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` (version-pin 11 → 12)
- Modify: `EverythingBox.Server.Core.Tests/MetadataContractTests.cs` (version-pin 11 → 12, add `[InlineData(1, 11)]`)
- Test: `EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs` (field-default test)

**Interfaces:**
- Consumes: `MemberChecksum`, `ChecksumVerifier.MatchesAsync` (Task 1).
- Produces: `TorrentResult.ExpectedChecksums` (`IReadOnlyList<MemberChecksum>`, default `[]`).

- [ ] **Step 1: Write the failing field-default test**

Append to the `MonoTorrentDownloaderTests` class in `EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs`:

```csharp
    [Fact]
    public void A_TorrentResult_defaults_to_no_expected_checksums()
    {
        // Additive field: existing producers get the empty default, so verification is off
        // and does zero hashing unless a producer opts in.
        var r = new TorrentResult { Title = "x", ProviderName = "p" };
        Assert.Empty(r.ExpectedChecksums);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~defaults_to_no_expected_checksums" -v minimal`
Expected: FAIL — build error, `TorrentResult` has no `ExpectedChecksums`.

- [ ] **Step 3: Add the `ExpectedChecksums` field**

In `EverythingBox.Server.Abstractions/Results/TorrentResult.cs`, add after the `WantedMembers` property:

```csharp
    /// <summary>
    /// Expected checksums for downloaded members. When a downloaded file matches an entry here
    /// (by filename), the self-download path verifies it and refuses to publish a mismatch.
    /// Empty (the default) skips verification entirely. Only the self-download (MonoTorrent)
    /// path consults this; debrid/direct paths are unaffected.
    /// </summary>
    public IReadOnlyList<MemberChecksum> ExpectedChecksums { get; init; } = [];
```

- [ ] **Step 4: Run the field test to verify it passes**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~defaults_to_no_expected_checksums" -v minimal`
Expected: PASS.

- [ ] **Step 5: Add verification to `ReleaseStreamResolver`**

In `EverythingBox.Server/Sources/ReleaseStreamResolver.cs`:

(a) Add `using EverythingBox.Server.Download;` at the top if `ChecksumVerifier` is not already in scope (only add if the build complains).

(b) Replace the publish loop in `TryFallbackDownloadAsync` (currently ~`:282-288`, the `foreach (var path in localPaths) { var built = await PublishAsync(...); ... }`) with:

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

(c) Change the working-directory cleanup guard (currently ~`:290`, `if (streams.Count > 0)`) so it runs whenever something was downloaded, cleaning rejected files too:

```csharp
        // Whatever was downloaded, the .downloads working copy has no reseeding use — PublishAsync
        // already moved every PUBLISHED file out, so this clears the rest: rejected files, unselected
        // pieces, engine scratch. Runs even when verification rejected everything.
        if (localPaths.Count > 0)
            RemoveDownloadDirectory(DownloadDirectory(files, release, request));

        return streams.Count > 0 ? streams : null;
```

(d) Add the `VerifyAsync` and `MemberMatches` helpers to the class (place them near `PublishAsync`):

```csharp
    /// <summary>
    /// True when <paramref name="localPath"/> may be published: either the release gave no
    /// expected checksum matching it, or its content matches the expected one. A read error is a
    /// failure — never publish something we could not verify when a checksum was demanded.
    /// </summary>
    private async Task<bool> VerifyAsync(TorrentResult release, string localPath, CancellationToken cancellationToken)
    {
        if (release.ExpectedChecksums.Count == 0)
            return true;

        var fileName = Path.GetFileName(localPath);
        var expected = release.ExpectedChecksums.FirstOrDefault(c => MemberMatches(c.Member, fileName));
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
    // Member may be a bare filename or a full member path; either way its last path segment is
    // compared to the downloaded file's name (MonoTorrent lays each member down under its own name).
    private static bool MemberMatches(string member, string fileName)
    {
        var m = member.Replace('\\', '/');
        var last = m.LastIndexOf('/');
        var memberName = last >= 0 ? m[(last + 1)..] : m;
        return string.Equals(memberName, fileName, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 6: Bump the API version and update the version-pinned tests**

In `EverythingBox.Server.Abstractions/ServerApi.cs`, change `VersionString`:
```csharp
    public const string VersionString = "1.12";
```

In `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs`, replace the version-pin test (asserting `Minor` 11):
```csharp
    [Fact]
    public void Version_is_1_12_now_that_the_download_path_can_verify_checksums()
    {
        Assert.Equal(1, ServerApi.Current.Major);
        Assert.Equal(12, ServerApi.Current.Minor);
    }
```

In `EverythingBox.Server.Core.Tests/MetadataContractTests.cs`, replace its version-pin test similarly:
```csharp
    [Fact]
    public void ApiVersion_is_1_12_now_that_the_download_path_can_verify_checksums()
    {
        Assert.Equal(1, ServerApi.Current.Major);
        Assert.Equal(12, ServerApi.Current.Minor);
    }
```
And add `[InlineData(1, 11)]` to the `Plugins_built_against_any_earlier_minor_still_load` theory (keep the existing rows).

- [ ] **Step 7: Run the full engine suites**

Run: `dotnet test EverythingBox.Server.Tests -v minimal`
Then: `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both PASS — including `RepositoryCleanlinessTests`, the updated version-pin tests, and the compat theory. If any OTHER test pinned the version to 1.11, update it to 1.12 the same way and note it.

- [ ] **Step 8: Commit**

```bash
git add EverythingBox.Server.Abstractions/Results/TorrentResult.cs EverythingBox.Server/Sources/ReleaseStreamResolver.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs
git commit -m "feat: verify a self-downloaded file against its expected checksum before publish (API 1.12)"
```

---

## Self-review

**Spec coverage:**
- `ChecksumAlgorithm` enum + `MemberChecksum` record (spec §1) → Task 1 Step 3. ✅
- `ChecksumVerifier` pure helper, streamed, built-in algorithms (spec §2) → Task 1 Step 4. ✅
- `TorrentResult.ExpectedChecksums` field (spec §1) → Task 2 Step 3. ✅
- `VerifyAsync` before publish, skip mismatch, fail-closed on read error, no-expectation short-circuit (spec §3 + Failure semantics) → Task 2 Step 5(b),(d). ✅
- Clean working dir even when everything rejected (spec §4) → Task 2 Step 5(c). ✅
- Match by filename, case-insensitive, separator-agnostic (spec §3) → `MemberMatches` in Task 2 Step 5(d). ✅
- Built-in algorithms only, no dependency (spec "Algorithms"/"What binds") → Task 1 Step 4 uses `System.Security.Cryptography`; no `PackageReference` added. ✅
- API 1.11 → 1.12 + version-pin/compat (spec "Testing"/"What binds") → Task 2 Step 6. ✅
- `DownloadAsync` unchanged; composes with #19/#5 (spec) → no task changes `DownloadAsync`; verification runs after the existing download/cap path. ✅
- Producer that SETS `ExpectedChecksums` OUT OF SCOPE → no task adds one. ✅

**Placeholder scan:** none — every code step shows complete code; digests are concrete known values for ASCII "abc".

**Type consistency:** `ChecksumVerifier.MatchesAsync(Stream, ChecksumAlgorithm, string, CancellationToken)` defined in Task 1, called with `(stream, expected.Algorithm, expected.Hex, cancellationToken)` in Task 2 Step 5(d) — matches. `MemberChecksum(string Member, ChecksumAlgorithm Algorithm, string Hex)` — `.Member/.Algorithm/.Hex` used consistently. `ExpectedChecksums` is `IReadOnlyList<MemberChecksum>` in the field (Task 2 Step 3) and consumed as such in `VerifyAsync`. Version strings consistent: `"1.12"`, `Minor == 12`, compat `[InlineData(1, 11)]`.
