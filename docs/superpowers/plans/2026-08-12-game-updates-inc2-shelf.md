# Game updates & DLC — Increment 2 (wire grouping into the RomLibrary shelf) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The RomLibrary shelf collapses a title's base + update + DLC into ONE expandable base `game`; drilling it lists the members (each streamable). `GroupUpdatesAndDlc=false` restores today's flat listing. Uses the Increment-1 `TitleGrouper`. Server-only, no API bump, no client change.

**Architecture:** `DetailAsync(platform-dir id)` groups the folder's ROM files via `TitleGrouper`; a group with members becomes an `Expandable` base game (subtitle = counts), a singleton stays a leaf. A new `DetailAsync(base-game id)` branch re-groups the base's folder and returns `{ base, update(s), dlc }` as leaf `game` items with roles. Gamelist titles/art on the base preserved; `ResolveAsync`/`OpenAsync`/`MetaAsync` unchanged.

**Tech Stack:** .NET 9 / C#, xUnit. `EverythingBox.Server.RomLibrary` + `EverythingBox.Server.Tests`.

## Global Constraints

- **Behaviour-preserving when off / ungrouped.** `GroupUpdatesAndDlc=false` ⇒ exactly today's flat per-file listing. A title with no update/DLC ⇒ a non-expandable leaf, as today. Ungrouped/unidentifiable files ⇒ leaves.
- **Group nothing when unsure** — inherited from `TitleGrouper` (a group forms only around a present base).
- **Members stream unchanged** — each base/update/DLC has id `EncodeId(path)` and serves through the existing `ResolveAsync`/`OpenAsync` proxy/Range path; containment unchanged.
- No API bump; plugin-only; BCL-only. PUBLIC repo cleanliness; no committed binary (runtime fixtures). Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: Group the platform listing + expand a grouped base

**Files:**
- Modify: `EverythingBox.Server.RomLibrary/RomLibraryConfig.cs` (add `GroupUpdatesAndDlc`)
- Modify: `EverythingBox.Server.RomLibrary/RomLibrarySource.cs` (ctor takes the flag; `DetailAsync` grouping + base-expansion branch)
- Modify: `EverythingBox.Server.RomLibrary/RomLibraryPlugin.cs` (pass the flag)
- Modify: `EverythingBox.Server.Tests/RomLibrarySourceTests.cs` (grouping tests)

**Interfaces:**
- Consumes: `TitleGrouper.Group`/`TitleGroup`/`GroupMember` (Increment 1).

- [ ] **Step 1: config flag** — `RomLibraryConfig`:
```csharp
    /// <summary>Group a title's base + update(s) + DLC into one expandable game (drill to the members).
    /// False = list every file flat (base-only-per-file). Default true.</summary>
    public bool GroupUpdatesAndDlc { get; set; } = true;
```
Thread it into the `RomLibrarySource` ctor (a `bool groupUpdatesAndDlc` param, stored in a field `_group`) and `RomLibraryPlugin.Configure` passes `config.GroupUpdatesAndDlc`.

- [ ] **Step 2: group the platform branch of `DetailAsync`.** Replace the per-file `foreach` (lines ~163-180) so that, when `_group`, files are grouped first:
```csharp
        var romPaths = Directory.EnumerateFiles(systemDir, "*", TopLevelFiles)
            .Where(p => IsRom(p) && _files.IsContained(p)).ToList();

        IEnumerable<(string BasePath, int UpdateCount, int DlcCount)> entries;
        if (_group)
        {
            entries = TitleGrouper.Group(romPaths)
                .Select(g => (g.BasePath, g.Updates.Count, g.Dlc.Count));
        }
        else
        {
            entries = romPaths.Select(p => (p, 0, 0));   // flat: every file is its own base, no members
        }

        foreach (var (basePath, updates, dlc) in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= MaxItems) { capped = true; break; }

            var meta = await _meta.GetOrComputeAsync<GameMeta>(basePath, gamelistPath,
                () => ComputeGameMeta(basePath, list), ct).ConfigureAwait(false);

            var hasMembers = updates + dlc > 0;
            items.Add(new CatalogItem(
                Id: SafeLocalFileServer.EncodeId(basePath),
                Title: meta.Name ?? Path.GetFileNameWithoutExtension(basePath),
                Subtitle: hasMembers ? MemberSummary(updates, dlc) : Path.GetFileName(basePath),
                Kind: MediaTypeNames.Game,
                ThumbnailUrl: BoxartUrl(meta.BoxartPath),
                Expandable: hasMembers));   // a grouped base drills into its members; a plain game is a leaf
        }
```
with a helper:
```csharp
    // "1 update", "2 DLC", "1 update · 2 DLC" — the drill-in hint on a grouped base game.
    private static string MemberSummary(int updates, int dlc)
    {
        var parts = new List<string>(2);
        if (updates > 0) parts.Add(updates == 1 ? "1 update" : $"{updates} updates");
        if (dlc > 0) parts.Add(dlc == 1 ? "1 DLC" : $"{dlc} DLC");
        return string.Join(" · ", parts);
    }
```
(The `Directory.EnumerateFiles` with `TopLevelFiles` still uses the hardened enumeration options — keep them. The `MaxItems` cap now counts groups, which is correct.)

- [ ] **Step 3: expand a grouped base — a new branch in `DetailAsync`.** Currently `DetailAsync` returns `Empty` when `ResolveSafeDir(itemId)` is null. Before that early return, add: if grouping is on and the id is a contained FILE that is a group base WITH members, return the members. Restructure the top of `DetailAsync`:
```csharp
    public async Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeDir(itemId) is { } systemDir)
            return await ListPlatformAsync(systemDir, ct).ConfigureAwait(false);   // the grouped platform listing (Step 2), extracted into a helper

        if (_group && _files.ResolveSafeFile(itemId) is { } basePath)
        {
            var dir = Path.GetDirectoryName(basePath);
            if (dir is not null)
            {
                var romPaths = Directory.EnumerateFiles(dir, "*", TopLevelFiles)
                    .Where(p => IsRom(p) && _files.IsContained(p)).ToList();
                var group = TitleGrouper.Group(romPaths)
                    .FirstOrDefault(g => string.Equals(g.BasePath, basePath, StringComparison.Ordinal)
                                      && (g.Updates.Count > 0 || g.Dlc.Count > 0));
                if (group is not null)
                    return MemberCatalog(group, dir, ct);
            }
        }

        return SourceCatalog.Empty("ROM Library");
    }
```
`MemberCatalog(group, dir, ct)` builds a `SourceCatalog` of leaf `game` items — the base first ("Base game"), then each update ("Update" + a version suffix when known, newest first — `group.Updates` is already newest-first), then each DLC ("DLC"):
```csharp
    private SourceCatalog MemberCatalog(TitleGroup group, string dir, CancellationToken ct)
    {
        var members = new List<CatalogItem>();
        members.Add(MemberItem(group.BasePath, "Base game"));
        foreach (var u in group.Updates)
            members.Add(MemberItem(u.Path, u.Version is { } v ? $"Update v{v}" : "Update"));
        foreach (var d in group.Dlc)
            members.Add(MemberItem(d.Path, "DLC"));
        var title = RomSystems.Resolve(Path.GetFileName(dir))?.Title ?? Path.GetFileName(dir);
        return new SourceCatalog(title, members);
    }
    private CatalogItem MemberItem(string path, string role) => new(
        Id: SafeLocalFileServer.EncodeId(path),
        Title: role,
        Subtitle: Path.GetFileName(path),
        Kind: MediaTypeNames.Game,
        ThumbnailUrl: null,
        Expandable: false);   // members are leaves — each streams by its own id
```
(Extract the Step-2 platform body into `ListPlatformAsync(systemDir, ct)`. `MetaAsync`/`ResolveAsync`/`OpenAsync` are UNCHANGED — a member's id resolves to its own file and serves normally; a member's meta panel is its own file's, which is fine.)

- [ ] **Step 4: tests** in `RomLibrarySourceTests.cs` — build a temp system folder (e.g. `snes/` or a switch-shaped folder; grouping is by title id from the filenames, so use grouped names) with a base `[0100AAAABBBB0000].nsp`, an update `[0100AAAABBBB0800].nsp` (+ `[v65536]`), a DLC `[0100AAAABBBB1000].nsp`, and one unrelated plain `Other Game.nsp`. Construct the source with `groupUpdatesAndDlc: true`. Assert:
  - `DetailAsync(<platform id>)` returns TWO items: the grouped base (Expandable, Subtitle "1 update · 1 DLC") and the plain game (not expandable). The three grouped files are NOT three separate leaves.
  - `DetailAsync(<base game id>)` returns THREE members: "Base game", "Update v65536", "DLC", each with a distinct id that `ResolveAsync` turns into a `proxy/romlib/…` stream.
  - `DetailAsync(<a member id, e.g. the DLC>)` returns empty (a member/plain game has nothing to expand).
  - a source constructed with `groupUpdatesAndDlc: false` → `DetailAsync(<platform id>)` lists all FOUR files flat, none expandable (today's behaviour).
  - the base with no update/DLC (the "Other Game") is a non-expandable leaf.
  - (reuse the existing RomLibrary test fixture/helpers; synthesize the .nsp files as tiny byte content — grouping is by filename, so content is irrelevant; no committed binary.)

- [ ] **Step 5: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green (the existing RomLibrary tests still pass — a folder of plain games with no title-id grouping lists flat because each is its own singleton base with no members).
```bash
git add EverythingBox.Server.RomLibrary/RomLibraryConfig.cs EverythingBox.Server.RomLibrary/RomLibrarySource.cs EverythingBox.Server.RomLibrary/RomLibraryPlugin.cs EverythingBox.Server.Tests/RomLibrarySourceTests.cs
git commit -m "feat: RomLibrary groups a title's base+update+DLC into one expandable game (drill to members)"
```

---

## Self-review

**Spec coverage (Increment 2):** `GroupUpdatesAndDlc` config, default true, false = flat (spec) → Steps 1,2,4. Platform listing collapses groups → one expandable base with a member-count subtitle (spec) → Step 2. Drilling a base lists `{base, update(s) newest-first, dlc}` members, each streamable (spec) → Step 3. Singletons/ungrouped stay leaves; a member/plain game doesn't expand (spec) → Steps 2,3,4. `ResolveAsync`/`OpenAsync`/`MetaAsync` unchanged; no API bump (spec) → untouched. ✅

**Placeholder scan:** the grouped-listing and base-expansion branches are complete code; `MemberSummary`/`MemberItem`/`MemberCatalog` are spelled out; the flat-when-off path is explicit; tests enumerate concrete assertions incl. the off path and the ungrouped-leaf path.

**Type consistency:** `TitleGrouper.Group → IReadOnlyList<TitleGroup>` with `BasePath`/`Updates`/`Dlc`(+`GroupMember.Version`) from Increment 1, consumed in both `DetailAsync` branches. `RomLibrarySource` ctor gains `bool groupUpdatesAndDlc`; `RomLibraryPlugin` passes `config.GroupUpdatesAndDlc`. `CatalogItem` uses `Kind:` (post-#4). `ResolveSafeDir`/`ResolveSafeFile`/`EncodeId`/`IsContained`/`TopLevelFiles`/`GameMeta`/`ComputeGameMeta`/`BoxartUrl` are existing members reused. ✅
