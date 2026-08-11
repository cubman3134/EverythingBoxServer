# ROM Library — Increment 1 (extract the shared local-file primitive) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract #8's Range serving + path-containment security + parse cache into reusable `EverythingBox.Server.Abstractions` types, and refactor `LocalLibrary` to use them — byte-identical behavior — so a coming ROM plugin shares one audited copy.

**Architecture:** A public `SafeLocalFileServer` (over `(roots, mimeFor)`) owns id-minting, containment, and Range serving; `RangeRequest` is public; `BoundedReadStream` is internal to Abstractions; `LibraryMetaCache` becomes generic. `LocalLibrarySource` delegates to them. No ROM code; no serving-behavior change.

**Tech Stack:** .NET 9 / C#, xUnit. `EverythingBox.Server.Abstractions` + `EverythingBox.Server.LocalLibrary`. No new package.

## Global Constraints

- **This is a MOVE of security-critical code — move the existing logic VERBATIM.** Read `LocalLibrarySource.cs`'s current `EncodeId`/`TryDecodeId`/`PathComparison`/`ResolveReal`/`IsContained`/`ResolveSafePath`/`ResolveSafeDir`/`OpenAsync`, `RangeRequest.cs`, and `BoundedReadStream.cs`, and relocate them character-for-character (only re-homing them into `SafeLocalFileServer` and parameterizing containment by the ctor `roots`). Do NOT re-derive or "improve" the containment.
- **Behavior byte-identical.** The whole point: `LocalLibrary`'s Inc 1–4 test suite must pass with NO assertion changes — only ctor/`EncodeId` call-site edits. If a `LocalLibrarySource` test needs an assertion change, the refactor drifted; fix the code, not the test.
- **Additive API bump 1.13 → 1.14** (new public Abstractions types); existing sources unaffected.
- **The tree must compile and all tests pass after EACH task** (no broken intermediate — each task does the Abstractions add + the plugin refactor + test retarget atomically; do not add a duplicate type to Abstractions while the plugin still has its own same-named type in a `using`d namespace).
- PUBLIC repo cleanliness — no external content-source name; `RepositoryCleanlinessTests` stays green.
- Stage by explicit path (never `git add -A`); no AI attribution. Run tests per-project.

---

### Task 1: `SafeLocalFileServer` + `RangeRequest` + `BoundedReadStream` in Abstractions; refactor LocalLibrary serving/security

**Files:**
- Create: `EverythingBox.Server.Abstractions/LocalFiles/RangeRequest.cs` (moved, public)
- Create: `EverythingBox.Server.Abstractions/LocalFiles/BoundedReadStream.cs` (moved, internal)
- Create: `EverythingBox.Server.Abstractions/LocalFiles/SafeLocalFileServer.cs` (new, absorbs the security+serving)
- Delete: `EverythingBox.Server.LocalLibrary/RangeRequest.cs`, `EverythingBox.Server.LocalLibrary/BoundedReadStream.cs`
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (delegate; remove the relocated methods)
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` (1.13 → 1.14) + `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` + `MetadataContractTests.cs`
- Create: `EverythingBox.Server.Tests/SafeLocalFileServerTests.cs`
- Move/retarget: `RangeRequestTests.cs`, `BoundedReadStreamTests.cs` (now exercise the Abstractions types)

**Interfaces:**
- Produces: `public sealed class SafeLocalFileServer` with ctor `(IReadOnlyList<string> roots, Func<string,string> mimeFor)`, `static string EncodeId(string)`, `static string? TryDecodeId(string)`, `bool IsContained(string)`, `string? ResolveSafeFile(string)`, `string? ResolveSafeDir(string)`, `Task<ProxyResponse?> OpenAsync(string id, string? rangeHeader, CancellationToken ct = default)`; `public` `RangeRequest`/`RangeResult`/`RangeKind`. All namespace `EverythingBox.Server.Abstractions`.

- [ ] **Step 1: Move `RangeRequest` + `BoundedReadStream` into Abstractions**

Copy `EverythingBox.Server.LocalLibrary/RangeRequest.cs` to `EverythingBox.Server.Abstractions/LocalFiles/RangeRequest.cs`: change the namespace to `EverythingBox.Server.Abstractions`, and `internal` → `public` on `RangeRequest`, `RangeResult`, `RangeKind`. Copy `BoundedReadStream.cs` to `EverythingBox.Server.Abstractions/LocalFiles/BoundedReadStream.cs`: namespace `EverythingBox.Server.Abstractions`, keep it `internal`. **Do not change any logic.** Then `git rm` the two plugin files.

- [ ] **Step 2: Create `SafeLocalFileServer` (absorb the security + serving verbatim)**

Create `EverythingBox.Server.Abstractions/LocalFiles/SafeLocalFileServer.cs`. Move, verbatim, from `LocalLibrarySource.cs`: `EncodeId`, `TryDecodeId` (static), `PathComparison`, `ResolveReal`, `IsContained`, `ResolveSafePath` (rename to `ResolveSafeFile`), `ResolveSafeDir`, and `OpenAsync`. Parameterize by ctor state:
```csharp
public sealed class SafeLocalFileServer
{
    private readonly IReadOnlyList<string> _roots;
    private readonly Func<string, string> _mimeFor;
    public SafeLocalFileServer(IReadOnlyList<string> roots, Func<string, string> mimeFor)
    { _roots = roots; _mimeFor = mimeFor; }
    // ... EncodeId/TryDecodeId (static, verbatim) ...
    // ... PathComparison/ResolveReal (private static, verbatim) ...
    // IsContained: iterate _roots (was _movieRoots.Concat(_seriesRoots) / _seriesRoots) — the caller
    //   scopes which roots by which SafeLocalFileServer instance it built, so the body iterates _roots.
    // ResolveSafeFile(id): verbatim ResolveSafePath body.
    // ResolveSafeDir(id): verbatim ResolveSafeDir body but iterate _roots (not a hardcoded _seriesRoots).
    // OpenAsync(id, rangeHeader, ct): verbatim OpenAsync body, but `path = ResolveSafeFile(id)` and
    //   `mime = _mimeFor(path)` (was the plugin's MimeFor). Uses RangeRequest + BoundedReadStream (now
    //   same-namespace). Returns 206/200/416/null exactly as today.
}
```
Critical: the containment logic (`ResolveReal` walking every reparse point, the trailing-separator
`StartsWith(resolvedRoot + Path.DirectorySeparatorChar, PathComparison)` boundary + `Equals` for a
file directly at a root, narrow `ArgumentException or PathTooLongException` catches) is **identical**.
`ResolveSafeDir` keeps the strict-subfolder rule (no `Equals(resolvedRoot)` acceptance) that Inc 2's
review-fix established.

- [ ] **Step 3: Refactor `LocalLibrarySource` to delegate**

Add fields and construct two servers (ids are static, so this only scopes containment):
```csharp
    private readonly SafeLocalFileServer _files;      // serving + file resolution over ALL roots
    private readonly SafeLocalFileServer _seriesDirs; // dir resolution scoped to SERIES roots
    // in the ctor, after _movieRoots/_seriesRoots are set:
    _files = new SafeLocalFileServer([.. _movieRoots, .. _seriesRoots], MimeFor);
    _seriesDirs = new SafeLocalFileServer(_seriesRoots, MimeFor);
```
Replace usages:
- `EncodeId(x)` → `SafeLocalFileServer.EncodeId(x)` (static; at every scan/detail/meta call site).
- `IsContained(path)` (the scan backstops) → `_files.IsContained(path)`.
- `ResolveSafePath(id)` (in `ResolveAsync`, `MetaAsync` file branch) → `_files.ResolveSafeFile(id)`.
- `ResolveSafeDir(id)` (in `DetailAsync`, `MetaAsync` dir branch) → `_seriesDirs.ResolveSafeDir(id)`.
- `OpenAsync(...)` body → `return _files.OpenAsync(itemId, rangeHeader, ct);` (the whole method delegates).
- `ResolveAsync`: keep building `proxy/{Key}/{itemId}/{name}`, but gate on `_files.ResolveSafeFile(itemId)`.
Then DELETE the now-relocated `EncodeId`/`TryDecodeId`/`PathComparison`/`ResolveReal`/`IsContained`/
`ResolveSafePath`/`ResolveSafeDir`/the `RangeRequest`/`BoundedReadStream` usage and the old `OpenAsync`
serving body from `LocalLibrarySource.cs`. Keep `MimeFor` (the plugin's video+image map — now passed
to the two ctors). Keep `MovieNfo`, `NfoReader`, `ArtworkFinder`, catalog logic, `PosterUrl`, `ItemMeta`.

- [ ] **Step 4: Retarget tests + add `SafeLocalFileServerTests`**

- Move `RangeRequestTests.cs`/`BoundedReadStreamTests.cs` to exercise the Abstractions types (change the `using` to `EverythingBox.Server.Abstractions`; `BoundedReadStream` is internal to Abstractions — add `[assembly: InternalsVisibleTo("EverythingBox.Server.Tests")]` to Abstractions if not present, OR make `BoundedReadStream` public; prefer InternalsVisibleTo to keep it internal — check whether Abstractions already exposes internals to the test project and mirror it).
- In `LocalLibrarySourceTests.cs`, change every `LocalLibrarySource.EncodeId(...)` call to `SafeLocalFileServer.EncodeId(...)` (a public static). NO assertion changes.
- Create `EverythingBox.Server.Tests/SafeLocalFileServerTests.cs` — the security surface directly over temp dirs: `EncodeId`/`TryDecodeId` round-trip; an out-of-roots id → `ResolveSafeFile`/`OpenAsync` null; a junction inside a root pointing outside → null; a root-prefix false match (`root` vs `root-Secret`) → null; `ResolveSafeDir` accepts a contained subdir, rejects a foreign dir and a file id; `OpenAsync` 206 (correct `Content-Range` + sliced bytes), 200 (full + ctor mime), 416 (unsatisfiable). (Port the assertions that lived in `LocalLibrarySourceTests`' containment/serving tests.)

- [ ] **Step 5: API bump + version tests**

`ServerApi.VersionString` 1.13 → 1.14. Update the two version-pin tests to Minor 14 (rename, e.g. `…_1_14_now_that_a_shared_local_file_server_ships`); add `[InlineData(1, 13)]` to the compat theory.

- [ ] **Step 6: Full suites + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both green — the ENTIRE `LocalLibrarySource` suite (Inc 1–4) passes with only call-site edits, the new `SafeLocalFileServerTests` green, `RepositoryCleanlinessTests` + version tests green. If any `LocalLibrarySource` assertion fails, the extraction drifted — fix the moved code to match, never the test.
```bash
git add EverythingBox.Server.Abstractions/LocalFiles/RangeRequest.cs EverythingBox.Server.Abstractions/LocalFiles/BoundedReadStream.cs EverythingBox.Server.Abstractions/LocalFiles/SafeLocalFileServer.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server.LocalLibrary/RangeRequest.cs EverythingBox.Server.LocalLibrary/BoundedReadStream.cs EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Tests/SafeLocalFileServerTests.cs EverythingBox.Server.Tests/RangeRequestTests.cs EverythingBox.Server.Tests/BoundedReadStreamTests.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs
git commit -m "refactor: extract a shared SafeLocalFileServer (range serving + path containment) into Abstractions (API 1.14)"
```
(The `git rm`'d plugin files are staged as deletes by the explicit `git add` of their paths.)

---

### Task 2: Move `LibraryMetaCache` to Abstractions, generic

**Files:**
- Create: `EverythingBox.Server.Abstractions/LocalFiles/LibraryMetaCache.cs` (moved, public, generic)
- Modify: `EverythingBox.Server.LocalLibrary/LibraryMetaCache.cs` → keep ONLY the `ItemMeta` record (rename file to `ItemMeta.cs`), delete the cache class
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (`GetOrComputeAsync<ItemMeta>`)
- Move/retarget: `LibraryMetaCacheTests.cs`

**Interfaces:**
- Produces: `public sealed class LibraryMetaCache(IResolverCache? cache)` with `Task<T> GetOrComputeAsync<T>(string mediaPath, string? nfoPath, Func<T> compute, CancellationToken ct)`, namespace `EverythingBox.Server.Abstractions`.

- [ ] **Step 1: Move + generalize the cache**

Create `EverythingBox.Server.Abstractions/LocalFiles/LibraryMetaCache.cs`: copy the current plugin `LibraryMetaCache` class verbatim (namespace `EverythingBox.Server.Abstractions`, `internal` → `public`), and make `GetOrComputeAsync` generic — `Task<T> GetOrComputeAsync<T>(string mediaPath, string? nfoPath, Func<T> compute, CancellationToken ct)` with `JsonSerializer.Deserialize<T>` / `Serialize` (T replaces `ItemMeta`). Key, best-effort semantics (read/write errors → recompute/swallow, `OperationCanceledException` always propagates) unchanged.

- [ ] **Step 2: Reduce the plugin file to `ItemMeta`**

In the plugin, delete the `LibraryMetaCache` class; keep the `ItemMeta` record (put it in `ItemMeta.cs`, or leave it in the same file with the cache class removed). `ItemMeta` stays `internal` to the plugin.

- [ ] **Step 3: Refactor `LocalLibrarySource` to the generic cache**

`_meta` is now the Abstractions `LibraryMetaCache`; every `_meta.GetOrComputeAsync(...)` call becomes `_meta.GetOrComputeAsync<ItemMeta>(...)` (four sites: `ScanMovies`, `ListShows`, `DetailAsync`, `MetaAsync`). No other change — the compute closures already return `ItemMeta`.

- [ ] **Step 4: Retarget the cache test**

Move `LibraryMetaCacheTests.cs` to exercise the Abstractions class: `using EverythingBox.Server.Abstractions`; the `MemoryCache` fake is unchanged; call `GetOrComputeAsync<SomeRecord>` (define a tiny test record, or reuse a public shape) for the hit/miss/mtime/corrupt/null cases. Assertions unchanged in intent.

- [ ] **Step 5: Full suites + commit**

Run both test projects — green incl. `RepositoryCleanlinessTests`, all `LocalLibrarySource` tests, the moved cache tests.
```bash
git add EverythingBox.Server.Abstractions/LocalFiles/LibraryMetaCache.cs EverythingBox.Server.LocalLibrary/LibraryMetaCache.cs EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.Tests/LibraryMetaCacheTests.cs
git commit -m "refactor: move the path+mtime metadata cache into Abstractions as a generic helper"
```
(If you renamed the plugin file to `ItemMeta.cs`, `git add` both the new and the deleted path.)

---

## Self-review

**Spec coverage:** `RangeRequest`/`BoundedReadStream`/`SafeLocalFileServer` in Abstractions (spec §1-3) → Task 1. Generic `LibraryMetaCache` (spec §4) → Task 2. LocalLibrary refactored to delegate, behavior-preserving (spec) → Task 1 Step 3 + Task 2 Step 3, gated by the unchanged `LocalLibrarySource` suite. API 1.13→1.14 + version tests (spec) → Task 1 Step 5. Security surface tested directly (spec Testing) → Task 1 Step 4 `SafeLocalFileServerTests`. No ROM code, no behavior change → constraints. ✅

**Placeholder scan:** none — the security code is a verbatim MOVE (explicitly "relocate character-for-character"), gated by the existing suite + a new direct security suite, which is safer than re-transcribing it inline; every call-site change is enumerated.

**Type consistency:** `SafeLocalFileServer(IReadOnlyList<string>, Func<string,string>)` + `EncodeId`(static)/`ResolveSafeFile`/`ResolveSafeDir`/`IsContained`/`OpenAsync` defined Task 1, consumed at the enumerated LocalLibrary call sites. `LibraryMetaCache.GetOrComputeAsync<T>` defined Task 2, called as `<ItemMeta>` at four sites. `ItemMeta` stays plugin-internal. Version `"1.14"`/Minor 14/`[InlineData(1,13)]` consistent.
