# Explicit-Member Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a caller specify exactly which member files of a torrent the self-download path fetches, via a new `TorrentResult.WantedMembers` field, bypassing the request heuristic when present.

**Architecture:** A pure, unit-testable `SelectMembers<T>` helper does the matching (full-path or filename, case-insensitive). `TorrentResult` gains an additive `WantedMembers` list (the source→host channel, so `ITorrentDownloader.DownloadAsync` is unchanged). `MonoTorrentDownloader.SelectWantedFiles` consults `WantedMembers` first and calls `SelectMembers`; when empty, today's `MediaFileMatcher` path is unchanged.

**Tech Stack:** .NET 9 / C#, xUnit. Two projects: `EverythingBox.Server.Abstractions` (contract) and `EverythingBox.Server` (host, has `InternalsVisibleTo("EverythingBox.Server.Tests")`). Tests in `EverythingBox.Server.Tests` + version-pin tests in `EverythingBox.Server.Core.Tests`.

## Global Constraints

- **PUBLIC repo — no content-source name anywhere** (code, comments, paths, test fixtures, or commit message). `RepositoryCleanlinessTests` scans contents + paths + commit message + full git history and fails on a denylisted string. Use neutral fixture names (`dir/one.bin`, `two.bin`, etc.) — never a real ROM-site/source name.
- **Single additive API bump 1.10 → 1.11.** `TorrentResult.WantedMembers` defaults to `[]` and `SelectMembers` runs only when the list is non-empty → no behavior change for any existing producer or path.
- **`ITorrentDownloader.DownloadAsync` signature is UNCHANGED** — the field rides on `TorrentResult`.
- `MediaFileMatcher` is untouched; the explicit path bypasses it.
- Stage files by explicit path (never `git add -A`). No AI attribution in any commit (no `Co-Authored-By`, no generated-by).
- No test spawns a process, touches the network, or reads a real browser profile.
- Run tests per-project — this CLI rejects two projects in one `dotnet test` call (MSB1008).

---

### Task 1: `SelectMembers<T>` — the pure matching helper

**Files:**
- Modify: `EverythingBox.Server/Download/MonoTorrentDownloader.cs` (add `SelectMembers<T>` + `FileName` near `SelectWantedFiles`, ~line 198)
- Test: `EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs` (append tests to the existing class)

**Interfaces:**
- Consumes: nothing (pure, generic over a file type `T` via a path accessor).
- Produces: `internal static IReadOnlyList<T> MonoTorrentDownloader.SelectMembers<T>(IReadOnlyList<T> files, Func<T,string> pathOf, IReadOnlyList<string> wantedMembers)` — used by Task 2's `SelectWantedFiles`.

- [ ] **Step 1: Write the failing tests**

Append to the `MonoTorrentDownloaderTests` class in `EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs`:

```csharp
    [Fact]
    public void SelectMembers_matches_a_full_member_path_exactly()
    {
        string[] files = ["dir/one.bin", "dir/two.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "dir/one.bin" });
        Assert.Equal(new[] { "dir/one.bin" }, picked);
    }

    [Fact]
    public void SelectMembers_matches_a_bare_filename_nested_in_a_directory()
    {
        string[] files = ["a/b/one.bin", "a/b/two.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "one.bin" });
        Assert.Equal(new[] { "a/b/one.bin" }, picked);
    }

    [Fact]
    public void SelectMembers_matches_case_insensitively()
    {
        string[] files = ["dir/One.BIN"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "one.bin" });
        Assert.Single(picked);
    }

    [Fact]
    public void SelectMembers_selects_several_members_in_file_order_not_wanted_order()
    {
        string[] files = ["one.bin", "two.bin", "three.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "three.bin", "one.bin" });
        Assert.Equal(new[] { "one.bin", "three.bin" }, picked);
    }

    [Fact]
    public void SelectMembers_returns_empty_when_nothing_matches()
    {
        string[] files = ["one.bin", "two.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, new[] { "nope.bin" });
        Assert.Empty(picked); // an all-miss selection yields nothing — caller downloads nothing, not everything
    }

    [Fact]
    public void SelectMembers_returns_empty_for_an_empty_wanted_list()
    {
        string[] files = ["one.bin"];
        var picked = MonoTorrentDownloader.SelectMembers(files, f => f, Array.Empty<string>());
        Assert.Empty(picked);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~SelectMembers" -v minimal`
Expected: FAIL — build error CS0117, `MonoTorrentDownloader` has no `SelectMembers`.

- [ ] **Step 3: Implement `SelectMembers` + `FileName`**

In `EverythingBox.Server/Download/MonoTorrentDownloader.cs`, add these members next to `SelectWantedFiles` (they use only `System.Linq`, already imported):

```csharp
    /// <summary>
    /// The subset of <paramref name="files"/> whose full member path OR filename (last path
    /// segment) equals one of <paramref name="wantedMembers"/>, compared case-insensitively.
    /// Order follows <paramref name="files"/>. A member that matches nothing contributes nothing,
    /// so an all-miss selection yields an empty list (the caller then downloads nothing, never
    /// the whole torrent).
    /// </summary>
    internal static IReadOnlyList<T> SelectMembers<T>(
        IReadOnlyList<T> files, Func<T, string> pathOf, IReadOnlyList<string> wantedMembers)
    {
        var wanted = new HashSet<string>(wantedMembers, StringComparer.OrdinalIgnoreCase);
        return files.Where(f =>
        {
            var path = pathOf(f);
            return wanted.Contains(path) || wanted.Contains(FileName(path));
        }).ToList();
    }

    // Last path segment, tolerant of both separators (torrent member paths use '/').
    private static string FileName(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~SelectMembers" -v minimal`
Expected: PASS — all six green.

- [ ] **Step 5: Commit**

```bash
git add EverythingBox.Server/Download/MonoTorrentDownloader.cs EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs
git commit -m "feat: add a pure member-selection helper for the self-download path"
```

---

### Task 2: `TorrentResult.WantedMembers` field, wiring, and API 1.11

**Files:**
- Modify: `EverythingBox.Server.Abstractions/Results/TorrentResult.cs` (add `WantedMembers`)
- Modify: `EverythingBox.Server/Download/MonoTorrentDownloader.cs` (`SelectWantedFiles` signature + call site)
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` (`VersionString` 1.10 → 1.11)
- Modify: `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` (version-pin 10 → 11)
- Modify: `EverythingBox.Server.Core.Tests/MetadataContractTests.cs` (version-pin 10 → 11, add `[InlineData(1, 10)]`)
- Test: `EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs` (field-default test)

**Interfaces:**
- Consumes: `MonoTorrentDownloader.SelectMembers<T>` (Task 1).
- Produces: `TorrentResult.WantedMembers` (`IReadOnlyList<string>`, default `[]`); `SelectWantedFiles(TorrentManager, MediaRequest?, IReadOnlyList<string>)`.

- [ ] **Step 1: Write the failing test**

Append to `MonoTorrentDownloaderTests` in `EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs`:

```csharp
    [Fact]
    public void A_TorrentResult_defaults_to_no_explicit_wanted_members()
    {
        // Additive field: existing producers that don't set it must get the empty default,
        // so the request-heuristic path stays the default.
        var r = new TorrentResult { Title = "x", ProviderName = "p" };
        Assert.Empty(r.WantedMembers);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~defaults_to_no_explicit_wanted" -v minimal`
Expected: FAIL — build error CS0117, `TorrentResult` has no `WantedMembers`.

- [ ] **Step 3: Add the `WantedMembers` field**

In `EverythingBox.Server.Abstractions/Results/TorrentResult.cs`, add after the `ParsedInfo` property (the last non-computed member, ~line 37):

```csharp
    /// <summary>
    /// Explicit torrent member(s) to fetch — each entry a full in-torrent member path or a bare
    /// filename. When non-empty, the self-download path fetches exactly the matching members and
    /// ignores the request heuristic. Empty (the default) keeps request-driven matching. Only the
    /// self-download (MonoTorrent) path consults this.
    /// </summary>
    public IReadOnlyList<string> WantedMembers { get; init; } = [];
```

- [ ] **Step 4: Run the field test to verify it passes**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~defaults_to_no_explicit_wanted" -v minimal`
Expected: PASS.

- [ ] **Step 5: Wire `SelectWantedFiles` to consult `WantedMembers`**

In `EverythingBox.Server/Download/MonoTorrentDownloader.cs`:

(a) Change the call site (currently `var wanted = SelectWantedFiles(manager, request);`, ~line 86) to pass the field:
```csharp
            var wanted = SelectWantedFiles(manager, request, torrent.WantedMembers);
```

(b) Change `SelectWantedFiles` (~line 198) to:
```csharp
    private static IReadOnlyList<ITorrentManagerFile> SelectWantedFiles(
        TorrentManager manager, MediaRequest? request, IReadOnlyList<string> wantedMembers)
    {
        var files = manager.Files.ToList();

        // Explicit member selection wins over the request heuristic. An all-miss selection
        // returns empty here, so the caller's "nothing matched → download nothing" path fires
        // rather than falling back to the whole torrent.
        if (wantedMembers.Count > 0)
            return SelectMembers(files, f => f.Path, wantedMembers);

        return request is null
            ? files
            : MediaFileMatcher.Select(request, files, f => f.Path, f => (long?)f.Length);
    }
```

- [ ] **Step 6: Bump the API version and update the version-pinned tests**

In `EverythingBox.Server.Abstractions/ServerApi.cs`, change `VersionString`:
```csharp
    public const string VersionString = "1.11";
```

In `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs`, replace the version-pin test (the one asserting `Major` 1 / `Minor` 10):
```csharp
    [Fact]
    public void Version_is_1_11_now_that_TorrentResult_carries_explicit_wanted_members()
    {
        Assert.Equal(1, ServerApi.Current.Major);
        Assert.Equal(11, ServerApi.Current.Minor);
    }
```

In `EverythingBox.Server.Core.Tests/MetadataContractTests.cs`, replace its version-pin test similarly:
```csharp
    [Fact]
    public void ApiVersion_is_1_11_now_that_TorrentResult_carries_explicit_wanted_members()
    {
        Assert.Equal(1, ServerApi.Current.Major);
        Assert.Equal(11, ServerApi.Current.Minor);
    }
```
And add `[InlineData(1, 10)]` to the `Plugins_built_against_any_earlier_minor_still_load` theory (it enumerates every earlier minor; 1.10 is now earlier), keeping the existing rows.

- [ ] **Step 7: Run the full engine suites**

Run: `dotnet test EverythingBox.Server.Tests -v minimal`
Then: `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both PASS — including `RepositoryCleanlinessTests`, the updated version-pin tests, and the compat theory. If any OTHER test pinned the version to 1.10, update it to 1.11 the same way and note it.

- [ ] **Step 8: Commit**

```bash
git add EverythingBox.Server.Abstractions/Results/TorrentResult.cs EverythingBox.Server/Download/MonoTorrentDownloader.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Tests/MonoTorrentDownloaderTests.cs
git commit -m "feat: honor explicit TorrentResult.WantedMembers in the self-download path (API 1.11)"
```

---

## Self-review

**Spec coverage:**
- `TorrentResult.WantedMembers` field (spec "The channel") → Task 2 Step 3. ✅
- `SelectMembers` pure helper + matching semantics full-path-or-filename, case-insensitive (spec "The pure testable core" + "Matching semantics") → Task 1. ✅
- `SelectWantedFiles` consults `WantedMembers` first, bypassing `MediaFileMatcher` (spec "Honoring it") → Task 2 Step 5. ✅
- No-match → empty → "nothing to download" (spec "No-match is honest") → covered by `SelectMembers_returns_empty_when_nothing_matches` + the Step 5 guard comment. ✅
- `DownloadAsync` signature unchanged; `MediaFileMatcher` untouched (spec "What binds") → no task changes them. ✅
- API 1.10 → 1.11 + version-pin/compat tests (spec "Testing"/"What binds") → Task 2 Step 6. ✅
- Size-cap composition (spec "Composition with EBS#5") → unchanged code path (selection still precedes the existing cap re-check); no task alters it, so it holds by construction. ✅
- Producer that SETS `WantedMembers` is OUT OF SCOPE (spec) → no task adds one. ✅

**Placeholder scan:** none — every code step shows complete code; test fixtures are concrete neutral names.

**Type consistency:** `SelectMembers<T>(IReadOnlyList<T>, Func<T,string>, IReadOnlyList<string>)` defined in Task 1, called with `(files, f => f.Path, wantedMembers)` in Task 2 Step 5 — matches. `WantedMembers` is `IReadOnlyList<string>` in both the field (Task 2 Step 3) and the `SelectWantedFiles` parameter (Task 2 Step 5). Version strings consistent: `"1.11"`, `Minor == 11`, compat `[InlineData(1, 10)]`.
