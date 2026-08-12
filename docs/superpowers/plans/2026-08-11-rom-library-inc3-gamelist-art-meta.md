# ROM Library — Increment 3 (gamelist.xml names, boxart, game meta panel) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give RomLibrary's games real names, boxart, and a meta panel by reading each system folder's `gamelist.xml` (ES-DE/RetroBat), with sibling-file art fallback — plugin-only, no API bump; boxart rides the existing proxy/Range path, every art path containment-checked.

**Architecture:** Two new pure units in the plugin — `GamelistStore` (XXE-safe `gamelist.xml` parse, indexed by `<path>` filename) and `RomArtFinder` (sibling boxart discovery). `RomLibrarySource` gains a cache (like #8 Inc 4) and wires gamelist name/desc/art/facts onto game items + `MetaAsync`. One shared-code edit: `LibraryMetaCache` gets a `typeof(T)` discriminator in its key (the Minor carried since Inc 1); LocalLibrary's suite is the parity gate.

**Tech Stack:** .NET 9 / C#, `System.Xml`/`XDocument`, xUnit. Plugin `EverythingBox.Server.RomLibrary`; shared `EverythingBox.Server.Abstractions`; tests in `EverythingBox.Server.Tests`.

## Global Constraints

- **No API bump.** #8 Inc 3 already shipped `SourceDetail`/`MetaFact`/`IMediaSource.MetaAsync` + the `/meta` route at engine **1.14**. This increment only IMPLEMENTS them and sets `ThumbnailUrl`. Do NOT touch `ServerApi.cs` or any version-pin test.
- **No new serving/containment code.** Boxart serves through the Inc-1 `SafeLocalFileServer.OpenAsync`; every art candidate — including a gamelist `<image>` — is validated with `_files.ResolveSafeFile` BEFORE it becomes a URL. A gamelist path escaping the roots must resolve to null and serve nothing.
- **XXE-safe parse.** `GamelistStore` uses the exact `NfoReader` settings (`DtdProcessing.Prohibit`, `XmlResolver = null`, `MaxCharactersFromEntities = 1024`), `XmlReader.Create(stream, settings)` → `XDocument.Load(reader)`, never `XDocument.Load(path)`; any failure → empty index (never throws out of a browse).
- **Behavior-preserving fallback.** A folder with no `gamelist.xml` browses exactly as Inc 2 (filename-stem titles, sibling art if any, else no art).
- **LocalLibrary stays green.** The `LibraryMetaCache` key change is best-effort (one-time recompute); no LocalLibrary source change; its full suite must pass unchanged.
- PUBLIC repo cleanliness — no external content-source name (code/paths/commit messages); `RepositoryCleanlinessTests` green. No new NuGet package. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: Type-discriminator in `LibraryMetaCache` (the Inc-1 carry)

**Files:**
- Modify: `EverythingBox.Server.Abstractions/LocalFiles/LibraryMetaCache.cs`
- Modify: `EverythingBox.Server.Tests/LibraryMetaCacheTests.cs`

**Interfaces:**
- Produces: unchanged signature `LibraryMetaCache.GetOrComputeAsync<T>(string, string?, Func<T>, CancellationToken)`; only the internal cache key now includes `typeof(T).FullName`.

- [ ] **Step 1: Write the failing test** — two different `T`s for the same (path, mtimes) must not collide.

Add to `LibraryMetaCacheTests.cs` (mirror the file's existing in-memory `IResolverCache` fake + temp-file setup; define two tiny records at file scope):
```csharp
private sealed record Alpha(string V);
private sealed record Beta(int N);

[Fact]
public async Task Different_value_types_for_the_same_path_do_not_collide()
{
    var cache = new MemoryResolverCache();               // the existing in-file fake
    var mc = new LibraryMetaCache(cache);
    var f = NewTempFile();                               // the existing temp-file helper

    var a = await mc.GetOrComputeAsync<Alpha>(f, null, () => new Alpha("a"), default);
    var b = await mc.GetOrComputeAsync<Beta>(f, null, () => new Beta(7), default);

    Assert.Equal("a", a.V);
    Assert.Equal(7, b.N);   // must NOT deserialize Alpha's JSON into Beta (defaulted N=0) or vice-versa
}
```
(Use the same fake/helper names the file already defines; read the file first and match them.)

- [ ] **Step 2: Run it, watch it fail** — `dotnet test EverythingBox.Server.Tests --filter LibraryMetaCacheTests -v minimal`. Expected: the new test fails (Beta reads Alpha's cached JSON → `N == 0`), the rest pass.

- [ ] **Step 3: Add the discriminator** to the key in `LibraryMetaCache.GetOrComputeAsync`:
```csharp
        var key = $"{typeof(T).FullName}|{mediaPath}|{File.GetLastWriteTimeUtc(mediaPath).Ticks}|" +
                  (nfoPath is null ? "0" : File.GetLastWriteTimeUtc(nfoPath).Ticks.ToString());
```
(Only the prefix `{typeof(T).FullName}|` is added; everything else is unchanged.)

- [ ] **Step 4: Run the whole Tests project** — `dotnet test EverythingBox.Server.Tests -v minimal`. Expected: green — the new collision test passes, all existing `LibraryMetaCacheTests` cases pass, and the full `LocalLibrarySource` suite passes unchanged (the key change is transparent to it).

- [ ] **Step 5: Commit**
```bash
git add EverythingBox.Server.Abstractions/LocalFiles/LibraryMetaCache.cs EverythingBox.Server.Tests/LibraryMetaCacheTests.cs
git commit -m "fix: key the metadata cache by value type so different shapes can't collide"
```

---

### Task 2: `GamelistStore` and `RomArtFinder`

**Files:**
- Create: `EverythingBox.Server.RomLibrary/GamelistStore.cs`
- Create: `EverythingBox.Server.RomLibrary/RomArtFinder.cs`
- Create: `EverythingBox.Server.Tests/GamelistStoreTests.cs`
- Create: `EverythingBox.Server.Tests/RomArtFinderTests.cs`

**Interfaces:**
- Produces: `GamelistStore.Load(string systemDir) → GamelistIndex`; `GamelistIndex.ForRom(string romPath) → GameEntry?`; `record GameEntry(string? Name, string? Desc, int? Year, string? Developer, string? Publisher, string? Genre, string? Players, string? ImageRelPath)`. `RomArtFinder.BoxartFor(string romPath) → string?` (absolute path or null).

- [ ] **Step 1: `GamelistStore.cs`** — XXE-safe parse, indexed by `<path>` filename.

```csharp
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace EverythingBox.Server.RomLibrary;

/// <summary>One game's fields read from a gamelist.xml entry. Image is the RAW relative path as written
/// in the gamelist (resolved + containment-checked by the caller, never trusted here).</summary>
internal sealed record GameEntry(
    string? Name, string? Desc, int? Year,
    string? Developer, string? Publisher, string? Genre, string? Players, string? ImageRelPath);

/// <summary>A parsed gamelist.xml, games indexed by the filename of their &lt;path&gt;.</summary>
internal sealed class GamelistIndex
{
    public static readonly GamelistIndex Empty = new(new Dictionary<string, GameEntry>());
    private readonly IReadOnlyDictionary<string, GameEntry> _byFileName;
    public GamelistIndex(IReadOnlyDictionary<string, GameEntry> byFileName) => _byFileName = byFileName;

    /// <summary>The entry for a ROM, matched by its file name (case-insensitive), or null.</summary>
    public GameEntry? ForRom(string romPath)
        => _byFileName.TryGetValue(Path.GetFileName(romPath), out var e) ? e : null;
}

/// <summary>
/// Reads a system folder's gamelist.xml (ES-DE / RetroBat) into an index keyed by the &lt;path&gt;
/// filename. XXE-safe (DTDs prohibited, no external resolver, entity expansion capped) exactly like
/// NfoReader; any failure (missing, malformed, disallowed DTD, I/O) → an empty index, never a throw.
/// </summary>
internal static class GamelistStore
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,   // 0 would mean UNLIMITED; backstop behind the prohibited DTD
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static GamelistIndex Load(string systemDir)
    {
        var gamelistPath = GamelistPath(systemDir);
        if (gamelistPath is null) return GamelistIndex.Empty;

        try
        {
            using var stream = File.OpenRead(gamelistPath);
            using var reader = XmlReader.Create(stream, Settings);
            var doc = XDocument.Load(reader);

            var map = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "game", StringComparison.OrdinalIgnoreCase)))
            {
                string? Child(string name) =>
                    g.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

                var path = Child("path");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var fileName = Path.GetFileName(path.Replace('\\', '/'));   // gamelist paths are ./sub/rom.ext
                if (string.IsNullOrWhiteSpace(fileName)) continue;

                var rd = Child("releasedate");           // yyyyMMddThhmmss
                int? year = rd is { Length: >= 4 } && int.TryParse(rd.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;

                var image = Child("image");
                if (string.IsNullOrWhiteSpace(image)) image = Child("thumbnail");

                // Last write wins if a gamelist lists a path twice — harmless, deterministic.
                map[fileName] = new GameEntry(
                    Name: Empty(Child("name")), Desc: Empty(Child("desc")), Year: year,
                    Developer: Empty(Child("developer")), Publisher: Empty(Child("publisher")),
                    Genre: Empty(Child("genre")), Players: Empty(Child("players")),
                    ImageRelPath: Empty(image));
            }
            return new GamelistIndex(map);
        }
        catch
        {
            return GamelistIndex.Empty;   // missing / malformed / disallowed DTD / I/O — all non-fatal
        }
    }

    /// <summary>The gamelist.xml for a system folder, or null. "gamelist.xml" then "miximages"-less common
    /// variants are not needed — ES-DE and RetroBat both use gamelist.xml in the system folder root.</summary>
    public static string? GamelistPath(string systemDir)
    {
        var p = Path.Combine(systemDir, "gamelist.xml");
        return File.Exists(p) ? p : null;
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
```

- [ ] **Step 2: `RomArtFinder.cs`** — sibling boxart discovery (modeled on `ArtworkFinder`).

```csharp
namespace EverythingBox.Server.RomLibrary;

/// <summary>Locates a boxart image for a ROM by ES-DE / RetroBat sibling-file conventions, when the
/// gamelist does not point at one. Returns an absolute path or null; the caller containment-checks it.</summary>
internal static class RomArtFinder
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    // Subfolders (relative to the system folder) that conventionally hold per-rom art, name == rom stem.
    private static readonly string[] ArtSubfolders = ["images", Path.Combine("media", "covers"), Path.Combine("media", "images")];
    private static readonly string[] FolderBaseNames = ["boxart", "folder"];

    public static string? BoxartFor(string romPath)
    {
        var dir = Path.GetDirectoryName(romPath);
        if (dir is null) return null;
        var stem = Path.GetFileNameWithoutExtension(romPath);

        // 1) "<stem>-image.<img>" next to the ROM.
        foreach (var ext in ImageExtensions)
        {
            var p = Path.Combine(dir, stem + "-image" + ext);
            if (File.Exists(p)) return p;
        }
        // 2) "<artsubfolder>/<stem>.<img>".
        foreach (var sub in ArtSubfolders)
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, sub, stem + ext);
                if (File.Exists(p)) return p;
            }
        // 3) folder-level "boxart.*" / "folder.*".
        foreach (var baseName in FolderBaseNames)
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, baseName + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
```

- [ ] **Step 3: `GamelistStoreTests.cs`** — write a small gamelist to a temp dir and assert:
  - a game's `Name`/`Desc`/`Year`(from `releasedate` `19960101T000000`→1996)/`Developer`/`Genre`/`Players` are read;
  - `ForRom` matches by filename with a `./` prefix and with a `subdir/` prefix in `<path>`;
  - `<image>` is preferred, `<thumbnail>` used when `<image>` absent;
  - a folder with no `gamelist.xml` → `GamelistIndex.Empty`, `ForRom` null;
  - **XXE:** a gamelist whose DOCTYPE declares an external entity used in `<name>` returns an entry whose `Name` does NOT contain the external content (the entity is not resolved) and does not throw — mirror `NfoReaderTests`' XXE case (read it first for the exact payload shape);
  - malformed XML → `GamelistIndex.Empty`, no throw.

- [ ] **Step 4: `RomArtFinderTests.cs`** — over temp dirs, assert the discovery order: `<stem>-image.png` beats an `images/<stem>.png`; `images/<stem>.jpg` found when no sibling; `media/covers/<stem>.png` found; folder `boxart.png` as last resort; no art → null.

- [ ] **Step 5: Run + commit**
Run: `dotnet test EverythingBox.Server.Tests --filter "GamelistStoreTests|RomArtFinderTests" -v minimal` → green.
```bash
git add EverythingBox.Server.RomLibrary/GamelistStore.cs EverythingBox.Server.RomLibrary/RomArtFinder.cs EverythingBox.Server.Tests/GamelistStoreTests.cs EverythingBox.Server.Tests/RomArtFinderTests.cs
git commit -m "feat: RomLibrary — gamelist.xml parser and sibling boxart discovery"
```

---

### Task 3: Wire names, boxart, meta panel, and the cache into `RomLibrarySource`

**Files:**
- Modify: `EverythingBox.Server.RomLibrary/RomLibrarySource.cs`
- Modify: `EverythingBox.Server.RomLibrary/RomLibraryPlugin.cs`
- Modify: `EverythingBox.Server.Tests/RomLibrarySourceTests.cs`

**Interfaces:**
- Consumes: `GamelistStore`/`GamelistIndex`/`GameEntry`, `RomArtFinder` (Task 2); `LibraryMetaCache` (Task 1); `SafeLocalFileServer`, `SourceDetail`, `MetaFact`.
- Produces: `RomLibrarySource(IReadOnlyList<string> roots, IResolverCache? cache, ILogger logger)`; `MetaAsync` implemented; game items carry gamelist titles + boxart `ThumbnailUrl`.

- [ ] **Step 1: Add the cache + a `GameMeta` record + a shared art-resolver** to `RomLibrarySource`.

Add ctor param and field (mirror #8 Inc 4; null cache in unit tests = Inc-2-identical):
```csharp
    private readonly LibraryMetaCache _meta;
    public RomLibrarySource(IReadOnlyList<string> roots, IResolverCache? cache, ILogger logger)
    {
        _roots = roots;
        _logger = logger;
        _files = new SafeLocalFileServer(roots, MimeFor);
        _meta = new LibraryMetaCache(cache);
    }
```
Add the record (raw parse + located art, so ONE entry serves scan + panel):
```csharp
    // The cached per-ROM parse: gamelist fields + the located boxart absolute path (null if none/uncontained).
    internal sealed record GameMeta(
        string? Name, string? Desc, int? Year,
        string? Developer, string? Publisher, string? Genre, string? Players, string? BoxartPath);
```
Add a helper that computes a `GameMeta` for a ROM given its folder's gamelist index (art: gamelist first, containment-checked, then sibling discovery, containment-checked):
```csharp
    private GameMeta ComputeGameMeta(string romPath, GamelistIndex list)
    {
        var e = list.ForRom(romPath);
        var systemDir = Path.GetDirectoryName(romPath);

        // 1) gamelist <image>/<thumbnail>, resolved relative to the system folder, containment-checked.
        string? art = null;
        if (e?.ImageRelPath is { } rel && systemDir is not null)
        {
            var candidate = Path.GetFullPath(Path.Combine(systemDir, rel.Replace('\\', '/')));
            if (_files.IsContainedFile(candidate)) art = candidate;
        }
        // 2) sibling discovery, containment-checked.
        if (art is null && RomArtFinder.BoxartFor(romPath) is { } sib && _files.IsContainedFile(sib))
            art = sib;

        return new GameMeta(e?.Name, e?.Desc, e?.Year, e?.Developer, e?.Publisher, e?.Genre, e?.Players, art);
    }

    private string? BoxartUrl(string? boxartPath) => boxartPath is null
        ? null
        : $"proxy/{Key}/{SafeLocalFileServer.EncodeId(boxartPath)}/{Uri.EscapeDataString(Path.GetFileName(boxartPath))}";
```
**Note on `IsContainedFile`:** `SafeLocalFileServer` exposes `IsContained(path)` (real-resolves + checks roots) but that method assumes the path exists. For an art candidate we must confirm BOTH existence and containment. Use `_files.ResolveSafeFile(SafeLocalFileServer.EncodeId(candidate))` — it does exactly Decode→GetFullPath→File.Exists→IsContained and returns the path or null. Replace the two `_files.IsContainedFile(x)` guards above with:
```csharp
        if (e?.ImageRelPath is { } rel && systemDir is not null)
        {
            var candidate = Path.Combine(systemDir, rel.Replace('\\', '/'));
            if (_files.ResolveSafeFile(SafeLocalFileServer.EncodeId(candidate)) is { } ok) art = ok;
        }
        if (art is null && RomArtFinder.BoxartFor(romPath) is { } sib
            && _files.ResolveSafeFile(SafeLocalFileServer.EncodeId(sib)) is { } okSib) art = okSib;
```
(No new `SafeLocalFileServer` method is added — this reuses the audited `EncodeId`/`ResolveSafeFile` round-trip, so a gamelist `<image>` of `../../secret.png` fails `File.Exists`-under-roots and yields null.)

- [ ] **Step 2: Make `DetailAsync` async and wire gamelist titles + boxart** onto game items.

Load the folder's gamelist ONCE, then per ROM compute (cached) a `GameMeta`; title = `Name ?? stem`; `ThumbnailUrl = BoxartUrl(meta.BoxartPath)`. Change the signature to `async Task<SourceCatalog>` and the gamelist file is the cache's shared "nfoPath":
```csharp
    public async Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeDir(itemId) is not { } systemDir)
            return SourceCatalog.Empty("ROM Library");

        var list = GamelistStore.Load(systemDir);
        var gamelistPath = GamelistStore.GamelistPath(systemDir);
        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var path in Directory.EnumerateFiles(systemDir, "*", TopLevelFiles))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRom(path)) continue;
            if (!_files.IsContained(path)) continue;
            if (items.Count >= MaxItems) { capped = true; break; }

            var meta = await _meta.GetOrComputeAsync<GameMeta>(path, gamelistPath,
                () => ComputeGameMeta(path, list), ct).ConfigureAwait(false);

            items.Add(new CatalogItem(
                Id: SafeLocalFileServer.EncodeId(path),
                Title: meta.Name ?? Path.GetFileNameWithoutExtension(path),
                Subtitle: Path.GetFileName(path),
                MediaType: "game",
                ThumbnailUrl: BoxartUrl(meta.BoxartPath),
                Expandable: false));
        }

        var title = RomSystems.Resolve(Path.GetFileName(systemDir))?.Title ?? Path.GetFileName(systemDir);
        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return new SourceCatalog(title, ordered, capped);
    }
```
(Note: the sort key is now the gamelist name when present — the human-visible title — which is the desired ordering.)

- [ ] **Step 3: Implement `MetaAsync`** (replace the default).

```csharp
    public async Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeFile(itemId) is not { } romPath)
            return null;

        var systemDir = Path.GetDirectoryName(romPath);
        var list = systemDir is null ? GamelistIndex.Empty : GamelistStore.Load(systemDir);
        var gamelistPath = systemDir is null ? null : GamelistStore.GamelistPath(systemDir);

        var meta = await _meta.GetOrComputeAsync<GameMeta>(romPath, gamelistPath,
            () => ComputeGameMeta(romPath, list), ct).ConfigureAwait(false);

        var facts = new List<MetaFact>(5);
        if (meta.Year is { } yr) facts.Add(new MetaFact("Year", yr.ToString()));
        if (meta.Genre is { } gn) facts.Add(new MetaFact("Genre", gn));
        if (meta.Players is { } pl) facts.Add(new MetaFact("Players", pl));
        if (meta.Developer is { } dv) facts.Add(new MetaFact("Developer", dv));
        if (meta.Publisher is { } pb) facts.Add(new MetaFact("Publisher", pb));

        return new SourceDetail(
            Title: meta.Name ?? Path.GetFileNameWithoutExtension(romPath),
            Overview: meta.Desc,
            ImageUrl: BoxartUrl(meta.BoxartPath),
            Facts: facts);
    }
```
(Confirm the `SourceDetail` ctor parameter names/order against `Catalog.cs`/the DTO — match #8's `MetaAsync` usage exactly: `SourceDetail(Title:, Overview:, ImageUrl:, Facts:)`. `Subtitle` is optional; omit it as #8's movie branch does when not needed, or pass the filename — match whatever the DTO requires to compile.)

- [ ] **Step 4: Wire the cache in `RomLibraryPlugin.Configure`** (mirror `LocalLibraryPlugin`):
```csharp
    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<RomLibraryConfig>() ?? new RomLibraryConfig();
        var cache = new FileResolverCache(Path.Combine(context.CacheDirectory, "meta"), 16L * 1024 * 1024);
        registry.AddSource(new RomLibrarySource(config.Roms, cache, context.Loggers.CreateLogger<RomLibrarySource>()));
    }
```

- [ ] **Step 5: Update `RomLibrarySourceTests`** for the new ctor + gamelist behavior.

- Every existing `new RomLibrarySource(roots, logger)` call → `new RomLibrarySource(roots, null, NullLogger<RomLibrarySource>.Instance)` (null cache = Inc-2-identical; existing assertions unchanged).
- Add: with a `gamelist.xml` in the `snes/` folder naming `Game A.sfc` as `"Super Game A"` with `<desc>`, `<genre>`, `<releasedate>`, and an `<image>./boxart/a.png</image>` that exists:
  - `DetailAsync` → the item's `Title == "Super Game A"`, `ThumbnailUrl` is `proxy/romlib/<EncodeId(a.png)>/a.png`.
  - `MetaAsync(<Game A id>)` → `Overview` = the desc, `ImageUrl` = the same boxart URL, `Facts` contain Year/Genre.
  - A game NOT in the gamelist → `Title` = stem, and if `GameB-image.png` sits beside it, `ThumbnailUrl` is its sibling art URL.
  - **Traversal:** a `gamelist.xml` whose `<image>` is `../../secret.png` (a file that exists OUTSIDE the roots) → the item's `ThumbnailUrl` is null and no proxy URL points outside the roots.
  - Boxart serves: `OpenAsync(<EncodeId(a.png)>, null, default)` → 200 with the image bytes.
- Use `NullLogger<RomLibrarySource>` and the temp-dir fixture already in the file.

- [ ] **Step 6: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green (incl. `RepositoryCleanlinessTests`, the full `RomLibrarySourceTests`, and LocalLibrary untouched).
```bash
git add EverythingBox.Server.RomLibrary/RomLibrarySource.cs EverythingBox.Server.RomLibrary/RomLibraryPlugin.cs EverythingBox.Server.Tests/RomLibrarySourceTests.cs
git commit -m "feat: RomLibrary — real names, boxart, and a game meta panel from gamelist.xml"
```

---

## Self-review

**Spec coverage:** `GamelistStore` XXE-safe parse + `<path>`-filename index (spec §1) → Task 2 Steps 1,3. Real titles (spec §2) → Task 3 Step 2. Boxart gamelist-first then sibling, containment-checked (spec §3) → Task 3 Step 1 (`ComputeGameMeta` + `ResolveSafeFile` guards), Task 2 Step 2. `MetaAsync` (spec §4) → Task 3 Step 3. Cache reuse + type discriminator (spec §5) → Task 1 + Task 3 Steps 1,4. LocalLibrary parity (spec) → Task 1 Step 4. Testing incl. XXE + traversal (spec) → Task 2 Step 3, Task 3 Step 5. No API bump / no new serving code / cleanliness (spec) → Global Constraints. ✅

**Placeholder scan:** none — every unit has full code; the one subtlety (existence+containment for an art candidate) is resolved concretely by the `EncodeId`→`ResolveSafeFile` round-trip (no invented `SafeLocalFileServer` method), and the `SourceDetail` ctor shape is pinned to #8's existing `MetaAsync` usage.

**Type consistency:** `RomLibrarySource(IReadOnlyList<string>, IResolverCache?, ILogger)` (Task 3) — all test call sites updated (Task 3 Step 5). `GameMeta`/`GamelistIndex`/`GameEntry`/`GamelistStore.Load`/`GamelistIndex.ForRom`/`RomArtFinder.BoxartFor` defined Task 2, consumed Task 3. `LibraryMetaCache.GetOrComputeAsync<T>` unchanged signature (Task 1), called as `<GameMeta>` (Task 3). `SourceDetail(Title,Overview,ImageUrl,Facts)` + `MetaFact(Label,Value)` match the #8 Inc-3 contract. `FileResolverCache`/`context.CacheDirectory`/`context.Loggers` match `LocalLibraryPlugin`. ✅
