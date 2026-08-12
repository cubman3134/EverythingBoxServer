# Game updates & DLC — Increment 1 (identification + grouping engine) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pure, unit-tested engine that identifies a console file's title identity (base / update / DLC) by release-naming convention (Switch / Wii U / 3DS / PS3) and by PS3 PKG header, then groups a folder's files into `{ base + updates + dlc }` sets — grouping ONLY around a present base, "group nothing" when unsure. No RomLibrary wiring yet (Increment 2).

**Architecture:** `TitleIdentifier.Identify(path)` returns a `PackageIdentity(TitleId, Kind, Version?)` from filename rules, overridden by `Ps3PkgReader` for `.pkg`. `TitleGrouper.Group(files)` aggregates identities by base title id. All internal to `EverythingBox.Server.RomLibrary`, BCL-only.

**Tech Stack:** .NET 9 / C#, xUnit. `EverythingBox.Server.RomLibrary` + `EverythingBox.Server.Tests`.

## Global Constraints

- **Identity over filename, but filename is the near-universal fallback.** A PS3 `.pkg`'s parsed content-id **overrides** the filename guess for its title id.
- **Group nothing when unsure.** Ambiguous/unidentifiable → the file is its own singleton; a group forms ONLY around a present Base (never invent a base for an orphan update/DLC).
- **BCL-only** — binary reads via `FileStream`/`BinaryReader`/`Span<byte>`; no NuGet. Core untouched (plugin code).
- **No committed binary fixtures** — tests synthesize named files (empty content for the naming path) and a minimal valid PS3 `.pkg` from an in-code `byte[]`; `RepositoryCleanlinessTests` (contents + history) stays green. No API bump. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: `PackageIdentity` + `TitleIdentifier` (naming rules + PS3 PKG reader)

**Files:**
- Create: `EverythingBox.Server.RomLibrary/TitleIdentity.cs` (`PackageIdentity`, `TitleKind`)
- Create: `EverythingBox.Server.RomLibrary/TitleIdentifier.cs`
- Create: `EverythingBox.Server.RomLibrary/Ps3PkgReader.cs`
- Create: `EverythingBox.Server.Tests/TitleIdentifierTests.cs`

**Interfaces:**
- Produces: `enum TitleKind { Base, Update, Dlc }`; `sealed record PackageIdentity(string TitleId, TitleKind Kind, int? Version)`; `static PackageIdentity? TitleIdentifier.Identify(string path)`; `static string? Ps3PkgReader.TryReadTitleId(string path)`.

- [ ] **Step 1: `TitleIdentity.cs`**
```csharp
namespace EverythingBox.Server.RomLibrary;
public enum TitleKind { Base, Update, Dlc }
/// <summary>What a single console file is. TitleId is the GROUP KEY (the base program id / game code);
/// Kind places the file within its title; Version orders updates (higher = newer), null if unknown.</summary>
public sealed record PackageIdentity(string TitleId, TitleKind Kind, int? Version);
```

- [ ] **Step 2: `Ps3PkgReader.cs`** — read the UNENCRYPTED PKG header content-id (the title id lives here; base/update/DLC classification is NOT reliably unencrypted, so the caller derives Kind from the filename):
```csharp
namespace EverythingBox.Server.RomLibrary;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Reads a PS3 .pkg's content-id from the unencrypted header (offset 0x30, 36 ASCII bytes),
/// e.g. "UP0001-BLES01807_00-0000000000000000", and extracts the game code ("BLES01807"). The decisive
/// base/patch/DLC category lives in an often-encrypted PARAM.SFO, so it is NOT read here — the game code
/// (the group key) is all we need unencrypted. Any malformed/short file → null, never throws.</summary>
internal static partial class Ps3PkgReader
{
    [GeneratedRegex(@"-([A-Z]{4}\d{5})_")] private static partial Regex GameCode();

    public static string? TryReadTitleId(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: false);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || magic[0] != 0x7F || magic[1] != (byte)'P' || magic[2] != (byte)'K' || magic[3] != (byte)'G')
                return null;
            fs.Seek(0x30, SeekOrigin.Begin);
            Span<byte> cid = stackalloc byte[36];
            if (fs.Read(cid) != 36) return null;
            var text = Encoding.ASCII.GetString(cid).TrimEnd('\0', ' ');
            var m = GameCode().Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; } // missing / short / IO — not a PKG we can read
    }
}
```

- [ ] **Step 3: `TitleIdentifier.cs`** — the naming rules, with the PS3 pkg override:
```csharp
namespace EverythingBox.Server.RomLibrary;
using System.Text.RegularExpressions;

internal static partial class TitleIdentifier
{
    [GeneratedRegex(@"(?<![0-9A-Fa-f])([0-9A-Fa-f]{16})(?![0-9A-Fa-f])")] private static partial Regex Hex16();
    [GeneratedRegex(@"\[v(\d+)\]|\bv(\d+)\b", RegexOptions.IgnoreCase)] private static partial Regex Version();
    [GeneratedRegex(@"\b(update|patch|upd)\b", RegexOptions.IgnoreCase)] private static partial Regex UpdateWord();
    [GeneratedRegex(@"\b(dlc|add[- ]?on)\b", RegexOptions.IgnoreCase)] private static partial Regex DlcWord();
    [GeneratedRegex(@"([A-Z]{4}\d{5})", RegexOptions.IgnoreCase)] private static partial Regex Ps3Code();

    /// <summary>Best identity for a file, or null if nothing plausible. Order: a 16-hex Switch/WiiU/3DS
    /// title id (the arithmetic relationship) → a PS3 .pkg header / PS3 game code in the name → generic
    /// update/DLC keywords. Null when no signal — the caller treats it as its own singleton.</summary>
    public static PackageIdentity? Identify(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var version = ParseVersion(name);

        // 1) 16-hex title id (Switch / Wii U / 3DS). The low bits place the file in its title.
        if (Hex16().Match(name) is { Success: true } h)
        {
            var id = h.Groups[1].Value.ToUpperInvariant();
            var high8 = id[..8];
            // Wii U / 3DS: the high 8 hex are the type; the base swaps them to the app type.
            if (high8 is "0005000E" or "0005000C")   // Wii U update / DLC
                return new PackageIdentity("00050000" + id[8..], high8 == "0005000E" ? TitleKind.Update : TitleKind.Dlc, version);
            if (high8 is "0004000E" or "0004008C")   // 3DS update / DLC
                return new PackageIdentity("00040000" + id[8..], high8 == "0004000E" ? TitleKind.Update : TitleKind.Dlc, version);
            // Switch (and Wii U/3DS base): base id = the id with its low 12 bits (3 nibbles) zeroed.
            var baseId = id[..13] + "000";
            var low = id[13..];   // 3 hex nibbles
            var kind = low switch { "000" => TitleKind.Base, "800" => TitleKind.Update, _ => TitleKind.Dlc };
            return new PackageIdentity(baseId, kind, version);
        }

        // 2) PS3: prefer the .pkg header content-id; else a game code in the name.
        var ps3Id = ext == ".pkg" ? Ps3PkgReader.TryReadTitleId(path) : null;
        ps3Id ??= Ps3Code().Match(name) is { Success: true } p ? p.Groups[1].Value.ToUpperInvariant() : null;
        if (ps3Id is not null)
            return new PackageIdentity(ps3Id.ToUpperInvariant(), KindFromWords(name), version);

        // 3) Generic keyword fallback — only meaningful once a base with the SAME stem exists (the grouper
        // decides). Group key = the title stem with version/update/DLC markers stripped.
        var kindW = KindFromWords(name);
        if (kindW != TitleKind.Base)
            return new PackageIdentity(NormalizeStem(name), kindW, version);

        return null;   // a plain, unmarked file → no identity; the grouper makes it a singleton
    }

    private static TitleKind KindFromWords(string name)
        => DlcWord().IsMatch(name) ? TitleKind.Dlc : UpdateWord().IsMatch(name) ? TitleKind.Update : TitleKind.Base;
    private static int? ParseVersion(string name)
    { var m = Version().Match(name); if (!m.Success) return null; var g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value; return int.TryParse(g, out var v) ? v : null; }
    // strip (…)/[…] tags, version/update/dlc words → a normalizing stem so a base and its keyword-marked
    // update share a group key. Lowercased, alphanumerics only (mirror the client's cleanTitle intent).
    private static string NormalizeStem(string name) { /* remove ()/[] regions, UpdateWord/DlcWord/Version matches, then keep letters+digits lowercased */ }
}
```
(Implement `NormalizeStem` as described; keep it deterministic.)

- [ ] **Step 4: `TitleIdentifierTests.cs`** — synthesize files at runtime (name-only for the naming path; a byte[] PS3 pkg):
  - Switch base `Game [0100AAAABBBB0000].nsp` → `("0100AAAABBBB0000", Base, null)`; update `…0800].nsp` → `Update` same base; DLC `…1000].nsp` → `Dlc` same base.
  - Wii U update `[0005000E12345678].wud` → `("0005000012345678", Update)`; DLC `[0005000C…]` → `Dlc`.
  - 3DS update `[0004000E…].cia` → `Update`.
  - **PS3 pkg**: write a minimal `.pkg` (a `byte[]` with `7F 50 4B 47` at 0, and at offset 0x30 the ASCII `"UP0001-BLES01807_00-0000000000000000"` null-padded to 36) → `Identify` returns `("BLES01807", …)` from the header, overriding a misleading filename; a bare `Game (BLES01807) UPDATE.pkg` with a non-pkg body → falls back to the name's game code + `Update` from the keyword.
  - version: `[v65536]` / `v131072` → the parsed int.
  - a plain `Some Game.iso` → `Identify` returns null (no signal).
  - a malformed `.pkg` (wrong magic / truncated) → `Ps3PkgReader.TryReadTitleId` null, no throw.

- [ ] **Step 5: Build + test + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` (green incl. `TitleIdentifierTests`, `RepositoryCleanlinessTests`, Core-BCL-only).
```bash
git add EverythingBox.Server.RomLibrary/TitleIdentity.cs EverythingBox.Server.RomLibrary/Ps3PkgReader.cs EverythingBox.Server.RomLibrary/TitleIdentifier.cs EverythingBox.Server.Tests/TitleIdentifierTests.cs
git commit -m "feat: TitleIdentifier — base/update/DLC identity by naming convention + PS3 PKG content-id"
```

---

### Task 2: `TitleGrouper`

**Files:**
- Create: `EverythingBox.Server.RomLibrary/TitleGrouper.cs`
- Create: `EverythingBox.Server.Tests/TitleGrouperTests.cs`

**Interfaces:**
- Consumes: `PackageIdentity`/`TitleIdentifier` (Task 1).
- Produces: `sealed record TitleGroup(string BaseTitleId, string BasePath, IReadOnlyList<GroupMember> Updates, IReadOnlyList<GroupMember> Dlc)` + `sealed record GroupMember(string Path, int? Version)`; `static IReadOnlyList<TitleGroup> TitleGrouper.Group(IEnumerable<string> files)`.

- [ ] **Step 1: `TitleGrouper.cs`**
  - `Group(files)`: `Identify` each file. Bucket by `TitleId`. For each bucket:
    - If it has ≥1 `Base` file → one `TitleGroup` headed by the base (if several bases share an id, pick the largest file as the base, the rest are effectively duplicates — keep the largest, list none of the others, or attach them as… keep it simple: the largest base is `BasePath`), with its `Update`s (sorted by `Version` desc, newest first) and `Dlc`s.
    - If a bucket has NO base (orphan updates/DLC) → do NOT form a group: emit each orphan file as its OWN singleton `TitleGroup` (BaseTitleId = its id, BasePath = itself, no members) — "group nothing" / never invent a base.
  - Files that `Identify` returned null for → each a singleton `TitleGroup(BasePath = the file, no members)`.
  - Deterministic ordering (by base path). Pure; the only I/O is `Identify`'s PS3 header read.

- [ ] **Step 2: `TitleGrouperTests.cs`**:
  - a folder's files `[…0000].nsp` (base) + `[…0800].nsp` (update) + `[…1000].nsp` (DLC) → ONE group, BasePath = the base, 1 update, 1 DLC.
  - two updates (`[v65536]`, `[v131072]`) → `Updates` ordered newest-first (131072 before 65536).
  - an orphan update with NO base in the set → its own singleton group (no invented base), NOT attached to anything.
  - an unidentifiable `Some Game.iso` → a singleton group.
  - a PS3 base pkg + a `…UPDATE.pkg` sharing the game code → ONE group.
  - two distinct titles in one folder → two groups.

- [ ] **Step 3: Build + test + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` — green.
```bash
git add EverythingBox.Server.RomLibrary/TitleGrouper.cs EverythingBox.Server.Tests/TitleGrouperTests.cs
git commit -m "feat: TitleGrouper — group update/DLC under a present base, singletons otherwise"
```

---

## Self-review

**Spec coverage (Increment 1):** naming-convention identity for Switch/WiiU/3DS/PS3 + PS3 PKG override (spec) → Task 1. Group only around a present base, "group nothing"/singletons, newest-update flag (spec) → Task 2. Pure/BCL-only, runtime-synthesized fixtures incl. a PS3 pkg byte template (spec) → both tasks' tests. No RomLibrary wiring (that is Inc 2), no API bump. ✅

**Placeholder scan:** the identity rules are concrete (hex arithmetic + PS3 content-id offset 0x30 + keyword fallback); the one prose step (`NormalizeStem`) is fully specified in words; the PS3 header read is complete code. The grouper's base-required rule and ordering are explicit.

**Type consistency:** `PackageIdentity(TitleId, TitleKind, int?)`/`TitleIdentifier.Identify`/`Ps3PkgReader.TryReadTitleId` (Task 1) consumed by `TitleGrouper.Group → IReadOnlyList<TitleGroup>` with `TitleGroup`/`GroupMember` (Task 2), which Increment 2 will consume in `DetailAsync`. All `internal` to the plugin. No API/Abstractions change. ✅
