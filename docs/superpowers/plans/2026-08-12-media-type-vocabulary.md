# Media-type vocabulary cleanup (EBS#4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kill the `MediaType` enum ↔ `CatalogDescriptor.MediaType` CS0117 collision by renaming the string member to `Kind`; give every catalog protocol string one home (constants on `MediaTypeNames`); make `"music"` canonical and write down the vocabulary model — wire unchanged, behavior unchanged.

**Architecture:** Additive first (constants + docs, tree stays green), then the atomic member rename with a compiler-driven call-site sweep across the engine and in-repo plugins, then the same rename in the private downstream plugin repo. The catalog wire is hand-projected (`type = c.MediaType`), so the rename never touches the JSON.

**Tech Stack:** .NET 9 / C#, xUnit. Public repo `EverythingBoxServer` (Abstractions + engine + LocalLibrary/RomLibrary/SampleSource plugins) and a private downstream plugin repo (local path provided at execution time).

## Global Constraints

- **No wire change.** The catalog `type` JSON key and every protocol-string VALUE stay identical. Only C# member names and the location of string literals change. A wire-stability test is the gate.
- **The rename is ONLY on `CatalogDescriptor.MediaType` and `CatalogItem.MediaType`.** Do NOT rename `MediaType` on any other type (`ReleaseRecord`, `MediaRequest`, `MediaTypeDescriptor.Type`, the `MediaType` enum). The compiler is the safety net: after renaming the two record members, every genuine catalog usage becomes a CS error; usages on other types still compile.
- **Behavior-preserving.** No test assertion about behavior changes; only `.MediaType`→`.Kind` reads and magic-string→const substitutions. Existing suites are the gate.
- **API bump 1.14 → 1.15** (source-breaking public contract rename). All in-repo plugins + the private plugin recompile against 1.15.
- PUBLIC repo cleanliness — no external content-source name (code/paths/commit messages); `RepositoryCleanlinessTests` green. No new NuGet package. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: Protocol-string constants + the vocabulary model (additive, public)

**Files:**
- Modify: `EverythingBox.Server.Abstractions/MediaTypeNames.cs`
- Modify: `EverythingBox.Server.Abstractions/Catalog.cs` (doc comments only)
- Modify: `EverythingBox.Server.Core.Tests/MediaTypeNamesTests.cs`

**Interfaces:**
- Produces: `public const string` protocol names on `MediaTypeNames` (`Movie`,`Series`,`Comic`,`Manga`,`Book`,`Audiobook`,`Music`,`Game`,`Platform`); behavior of `ToProtocolString`/`TryParseProtocol` unchanged.

- [ ] **Step 1: Read `MediaTypeNames.cs`**, then add the constants and route the existing `ToProtocol`/`FromProtocol` dictionaries through them (single source of truth). Add the vocabulary-model doc comment. The constants:
```csharp
    // The canonical client protocol-string vocabulary — one home for every value that appears on a
    // CatalogDescriptor/CatalogItem. Use these instead of string literals at call sites.
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Comic = "comic";
    public const string Manga = "manga";
    public const string Book = "book";
    public const string Audiobook = "audiobook";
    public const string Music = "music";
    public const string Game = "game";
    public const string Platform = "platform";
```
Rewrite the dictionary literals to reference the consts, e.g. `[MediaType.Tv] = Series`, `[Series] = MediaType.Tv`, `[Manga] = MediaType.Comic`, `[MediaType.PcGame] = Game`, etc. — VALUES unchanged, only sourced from the consts. Do NOT add `Platform` to either dictionary (it is a client-only container type with no enum member — `TryParseProtocol("platform")` must keep returning false).

- [ ] **Step 2: Document the model** — add a class-level doc comment on `MediaTypeNames` capturing: the `MediaType` enum is the **pipeline** vocabulary; the protocol strings are the **client** vocabulary; this helper bridges their intersection; and the three non-overlap cases (`Music` = both, `Platform` = client-only container, `Other` = pipeline-only fallback with no protocol string, `manga`+`comic` both → `Comic`). Keep it concise.

- [ ] **Step 3: Make `"music"` canonical + note game/platform in `Catalog.cs`** — update the doc comments on `CatalogDescriptor` and `CatalogItem` so the listed protocol set is authoritative and complete: `"movie", "series", "comic", "manga", "book", "audiobook", "music", "game", "platform"`. (Comment-only; no code change in this file yet — the member rename is Task 2.)

- [ ] **Step 4: Extend `MediaTypeNamesTests`** — add:
  - each const equals its literal value (`Assert.Equal("movie", MediaTypeNames.Movie)` … for all nine);
  - `TryParseProtocol("platform")` returns **false** — with a comment that this is intentional (client-only container), a regression guard;
  - keep/confirm the existing round-trip + `manga`→`Comic` + `ToProtocolString(MediaType.Other) is null` cases.

- [ ] **Step 5: Run + commit**
Run: `dotnet test EverythingBox.Server.Core.Tests -v minimal` → green (this is additive; the whole solution still builds).
```bash
git add EverythingBox.Server.Abstractions/MediaTypeNames.cs EverythingBox.Server.Abstractions/Catalog.cs EverythingBox.Server.Core.Tests/MediaTypeNamesTests.cs
git commit -m "docs: protocol-string constants and the media-type vocabulary model on MediaTypeNames"
```

---

### Task 2: Rename the colliding member + sweep call sites + API bump (atomic, public)

**Files (rename target):**
- Modify: `EverythingBox.Server.Abstractions/Catalog.cs` — `CatalogDescriptor` + `CatalogItem` member `MediaType` → `Kind`
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` — `VersionString` 1.14 → 1.15
**Files (call-site sweep — the compiler will confirm the full set; known sites):**
- `EverythingBox.Server/AddonEndpoints.cs:396` (`type = i.MediaType` → `i.Kind`)
- `EverythingBox.Server/ManifestBuilder.cs:64` (`type = c.MediaType` → `c.Kind`)
- `EverythingBox.Server/Sources/IndexerSearchSource.cs` (`descriptor.MediaType`→`.Kind` at :87,:109,:137; `MediaType: mediaType`→`Kind:` at :166; **leave `record.MediaType` at :289 alone if `record` is not a CatalogItem** — verify type)
- `EverythingBox.Server/Sources/MetadataBackedVideoSource.cs` (`descriptor.MediaType`→`.Kind` at :84,:90,:98,:107; `item.MediaType`→`.Kind` at :107,:236,:296; `MediaType:`→`Kind:` at :236,:254; `Expandable: string.Equals(item.MediaType,"series",…)`→`item.Kind`; the `"series"`/`"movie"` literals → consts, below)
- `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (`MediaType:`→`Kind:` at :129,:183,:233)
- `EverythingBox.Server.RomLibrary/RomLibrarySource.cs` (`MediaType:`→`Kind:` at :114,:177)
- `EverythingBox.Server.SampleSource/LocalFolderSource.cs` (`MediaType:`→`Kind:` at :79)
- Tests reading `.MediaType` on catalog items: `IndexerSearchSourceTests.cs:142,156,217`, `LocalLibrarySourceTests.cs:46,54,168,178,201`, `MetadataBackedVideoSourceTests.cs:128,137`, `RomLibrarySourceTests.cs:29,40,54,86` → `.Kind`
- Version-pin tests: `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` + `MetadataContractTests.cs` → Minor 15; compat theory gains `[InlineData(1, 14)]`
- New: `EverythingBox.Server.Tests/CatalogWireStabilityTests.cs`

- [ ] **Step 1: Rename the two record members** in `Catalog.cs`: `CatalogDescriptor(string Id, string Name, string MediaType)` → `... string Kind)` and the `CatalogItem` `string MediaType` positional member → `string Kind` (keep its position — 4th, before `ThumbnailUrl`). Update the `<paramref>` doc names.

- [ ] **Step 2: Build and let the compiler enumerate the sweep**
Run: `dotnet build EverythingBoxServer.sln -c Debug -clp:ErrorsOnly`
Every error is a genuine catalog-type usage. Fix each:
- `.MediaType` reads on a `CatalogDescriptor`/`CatalogItem` → `.Kind`.
- named-arg constructions `MediaType:` on those records → `Kind:`.
- For any `.MediaType` that the compiler does NOT flag (it is on another type — e.g. a `ReleaseRecord`/`MediaRequest`), LEAVE IT. Rebuild until zero errors.
Confirm the two wire projections now read `type = c.Kind` (ManifestBuilder) and `type = i.Kind` (AddonEndpoints) — the JSON key `type` is unchanged.

- [ ] **Step 3: Replace the magic protocol strings with the Task-1 consts** (behavior identical):
- `MetadataBackedVideoSource.cs`: the `Supports(source, "movie")` / `Supports(source, "series")` (ctor ~:55,:57), `Supports(source, "series")` (~:127), `string.Equals(item.Kind, "series", …)` (~:240), and the hard-coded `Kind: "series"` in `ToEpisodeItem`/`EncodeEpisodeId` (~:254,:304) → `MediaTypeNames.Movie` / `MediaTypeNames.Series`.
- `RomLibrarySource.cs`: `Kind: "platform"` (:114) → `MediaTypeNames.Platform`; `Kind: "game"` (:177) and `new CatalogDescriptor("games","Games","game")` (:75, the third positional arg only — NOT the `"games"` id) → `MediaTypeNames.Game`. (RomLibrary already references `EverythingBox.Server.Abstractions`.)
- Leave the `"games"` catalog **id** and `LocalLibrary`'s `"movies"`/`"series"` catalog **ids** as literals — ids are not protocol types. (Optionally use `MediaTypeNames.Movie`/`.Series` for LocalLibrary's `Kind:` values; do so for consistency.)

- [ ] **Step 4: Bump the API version** — `ServerApi.VersionString` "1.14" → "1.15". Update `ServerApiContractTests`/`MetadataContractTests` version-pin assertions to Minor 15 (rename the test methods to say 1_15), and add `[InlineData(1, 14)]` to the backward-compat theory.

- [ ] **Step 5: Add the wire-stability test** `EverythingBox.Server.Tests/CatalogWireStabilityTests.cs` — prove the rename did not leak to the wire. Exercise the ACTUAL projections: build a `CatalogDescriptor(... Kind: "series")` and a `CatalogItem(... Kind: "series" ...)`, run them through the real manifest/catalog serialization path the routes use (call `ManifestBuilder`/the catalog endpoint projection, or serialize the same anonymous shape), and assert the emitted JSON contains `"type":"series"` (key `type`, value `series`) and NOT a key `"kind"` or `"mediaType"`. (Read how `ManifestBuilderTests`/`AddonEndpoints` tests invoke these to reuse the harness.)

- [ ] **Step 6: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green (all existing suites behavior-unchanged; new wire-stability + updated version tests pass; `RepositoryCleanlinessTests` green).
```bash
git add EverythingBox.Server.Abstractions/Catalog.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server/AddonEndpoints.cs EverythingBox.Server/ManifestBuilder.cs EverythingBox.Server/Sources/IndexerSearchSource.cs EverythingBox.Server/Sources/MetadataBackedVideoSource.cs EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.RomLibrary/RomLibrarySource.cs EverythingBox.Server.SampleSource/LocalFolderSource.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Tests/CatalogWireStabilityTests.cs EverythingBox.Server.Tests/IndexerSearchSourceTests.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs EverythingBox.Server.Tests/MetadataBackedVideoSourceTests.cs EverythingBox.Server.Tests/RomLibrarySourceTests.cs
git commit -m "refactor!: rename CatalogDescriptor/CatalogItem.MediaType to Kind, ending the enum name collision (API 1.15)"
```

---

### Task 3: Recompile the private downstream plugin against 1.15

**Repo:** the private downstream plugin repo (local path provided to the implementer at execution time). **Files (compiler-confirmed; the sweep is compiler-driven, so the exact paths do not need enumerating here):**
- Every source that builds a `CatalogItem`/`CatalogDescriptor` with a **named** `MediaType:` argument (the plugin's book, comic, manga, retro-game, and metadata sources) → `Kind:`.
- Any `.MediaType` READ on a `CatalogItem`/`CatalogDescriptor` (compiler will flag) → `.Kind`.
- Positional `CatalogDescriptor(id, name, "book"/"comic"/…)` constructions are UNAFFECTED (positional).

- [ ] **Step 1: Confirm the repo picks up the new Abstractions** — it references the public `EverythingBox.Server.Abstractions` (project or built output). Ensure the build resolves the just-changed 1.15 Abstractions (rebuild the public solution first if the plugin consumes a built DLL/package; if it is a ProjectReference to the sibling path, it resolves automatically).

- [ ] **Step 2: Build and sweep** — `dotnet build` the plugin solution with `-clp:ErrorsOnly`. Every error is a `CatalogItem`/`CatalogDescriptor` `MediaType`-member usage; change named `MediaType:` → `Kind:` and `.MediaType` reads → `.Kind`. Leave `MediaType.<enum member>` and `request.MediaType`/`SupportedMediaTypes` (enum) untouched. Rebuild until clean. (Optionally, but not required, replace the catalog-type string literals with `MediaTypeNames.*` consts.)

- [ ] **Step 3: Run the private plugin's tests** — `dotnet test` its test project(s) `-v minimal`. Expected: green (behavior unchanged; only the member name moved). The plugin's `ApiVersion => new(ServerApi.VersionString)` now reports 1.15 automatically.

- [ ] **Step 4: Commit (in the private plugin repo)**
```bash
git add <the changed .cs files by explicit path>
git commit -m "refactor: follow the Abstractions CatalogItem.MediaType -> Kind rename (API 1.15)"
```
(No AI attribution. This repo has its own cleanliness rules — no engine-internal names leak in; a plain member rename is fine.)

---

## Self-review

**Spec coverage:** protocol-string constants + model doc (spec §2, §"model") → Task 1. Member rename `MediaType`→`Kind`, wire hand-projected/unchanged (spec §1) → Task 2 Steps 1-2,5. Magic-string replacement (spec §2) → Task 2 Step 3. Music canonical + doc (spec §3) → Task 1 Step 3. API bump 1.15 + version tests (spec §"Host API") → Task 2 Step 4. Wire-stability + platform-false tests (spec Testing) → Task 1 Step 4, Task 2 Step 5. Cross-repo lockstep (spec) → Task 3. ✅

**Placeholder scan:** none — the rename sweep is compiler-driven with the full known site list enumerated and an explicit "leave non-catalog `.MediaType`" rule; every const and doc is spelled out. The one ambiguity (`IndexerSearchSource.cs:289 record.MediaType`) is called out with a "verify the type; leave if not a CatalogItem" instruction.

**Type consistency:** `CatalogDescriptor`/`CatalogItem` expose `Kind` after Task 2; every reader/constructor updated in the same task (compiler-enforced). `MediaTypeNames.{Movie,Series,Comic,Manga,Book,Audiobook,Music,Game,Platform}` defined Task 1, consumed Task 2/3. `Platform` deliberately absent from the dictionaries (TryParseProtocol false). `ServerApi.VersionString="1.15"`, version tests Minor 15 + `[InlineData(1,14)]`. Wire key stays `type`. ✅
