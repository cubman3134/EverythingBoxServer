# Local Library Plugin — Increment 2 (Series/TV) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add TV series to the `EverythingBox.Server.LocalLibrary` plugin: a `series` catalog listing shows, `DetailAsync` expanding a show into ordered episodes, and episode playback with Range — reusing Increment 1's serving + path security.

**Architecture:** Evolve `MovieLibrarySource` → `LocalLibrarySource` holding both movie and series roots and both catalogs. A file id (movie/episode) is served; a series id is a folder that `DetailAsync` expands. Containment spans the union of all roots. No host/contract change.

**Tech Stack:** .NET 9 / C#, xUnit. All changes in `EverythingBox.Server.LocalLibrary` + its tests in `EverythingBox.Server.Tests`.

## Global Constraints

- **Plugin-only.** No `EverythingBox.Server`/Abstractions/contract change, no `ServerApi` version bump, no new NuGet package.
- **Movie behavior is preserved byte-for-byte** through the rename — movie ids are unchanged, serving and security are reused, not rewritten.
- **Path security spans all roots.** Serving (`ResolveSafePath`/`IsContained`) must accept a file id contained in ANY configured root (movie or series). Expanding (`ResolveSafeDir`) must accept only a directory contained in a SERIES root. An id escaping the roots serves/expands nothing.
- **Fresh-checkout-serves-nothing holds:** `Catalogs` empty when no roots; `series` catalog only when series roots exist.
- **PUBLIC repo cleanliness:** the plugin names no external content source; keep code/paths/commit messages generic; `RepositoryCleanlinessTests` stays green.
- Episode/series shape matches `MetadataBackedVideoSource`: a series item is `Expandable`; episodes are a flat list, `MediaType "series"`, season in the title.
- Stage files by explicit path (never `git add -A`); no AI attribution.
- No test spawns a process, touches the network, or reads a real browser profile — temp dirs only.
- Run tests per-project (`dotnet test EverythingBox.Server.Tests` / `… .Core.Tests` separately).

---

### Task 1: Rename to `LocalLibrarySource`, add series roots + the `series` catalog/listing

**Files:**
- Rename: `EverythingBox.Server.LocalLibrary/MovieLibrarySource.cs` → `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (`git mv`), rename the class.
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibraryConfig.cs` (add `Series`)
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibraryPlugin.cs` (pass both root lists)
- Rename/modify test: `EverythingBox.Server.Tests/MovieLibrarySourceTests.cs` → `LocalLibrarySourceTests.cs` (update type + helper; add series-listing tests)

**Interfaces:**
- Produces: `LocalLibrarySource(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, ILogger logger)`; a `series` `CatalogDescriptor`; `SearchAsync("series", …)` listing show folders; `internal string? ResolveSafeDir(string itemId)`. `internal static EncodeId`/`ResolveSafePath` unchanged.

- [ ] **Step 1: `git mv` the source, rename the class, and thread series roots**

`git mv EverythingBox.Server.LocalLibrary/MovieLibrarySource.cs EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs`

In `LocalLibrarySource.cs`: rename `class MovieLibrarySource` → `class LocalLibrarySource`; update the `<summary>` to mention both movies and series; change the field/ctor to hold both root lists:
```csharp
    private readonly IReadOnlyList<string> _movieRoots;
    private readonly IReadOnlyList<string> _seriesRoots;
    private readonly ILogger _logger;

    public LocalLibrarySource(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, ILogger logger)
    {
        _movieRoots = movieRoots;
        _seriesRoots = seriesRoots;
        _logger = logger;
    }
```

`Catalogs` — conditional, both shelves:
```csharp
    public IReadOnlyList<CatalogDescriptor> Catalogs
    {
        get
        {
            var list = new List<CatalogDescriptor>(2);
            if (_movieRoots.Count > 0) list.Add(new CatalogDescriptor("movies", "Movies", "movie"));
            if (_seriesRoots.Count > 0) list.Add(new CatalogDescriptor("series", "Series", "series"));
            return list;
        }
    }
```

`IsContained` — iterate the UNION of roots. Change its loop header from `foreach (var folder in _movieRoots)` to iterate `_movieRoots.Concat(_seriesRoots)` (leave the body — real-resolve, trailing-separator boundary, `PathComparison` — exactly as is):
```csharp
        foreach (var folder in _movieRoots.Concat(_seriesRoots))
```

- [ ] **Step 2: `LocalLibraryConfig` + `LocalLibraryPlugin`**

In `LocalLibraryConfig.cs`, add:
```csharp
    /// <summary>Absolute paths to folders laid out as Show/Season NN/…; each immediate subfolder is a series.</summary>
    public List<string> Series { get; set; } = [];
```
In `LocalLibraryPlugin.cs`, change the registration to:
```csharp
        registry.AddSource(new LocalLibrarySource(config.Movies, config.Series, context.Loggers.CreateLogger<LocalLibrarySource>()));
```

- [ ] **Step 3: Update the test file (rename + helper), verify movie tests still pass**

`git mv EverythingBox.Server.Tests/MovieLibrarySourceTests.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs`. Rename the class to `LocalLibrarySourceTests`. Update every `MovieLibrarySource` reference to `LocalLibrarySource` and the `EncodeId` call site (`LocalLibrarySource.EncodeId`). Change the movie-only helper to pass empty series roots:
```csharp
    private LocalLibrarySource Movies(params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, [], NullLogger<LocalLibrarySource>.Instance);
```
Update the existing tests to call `Movies(...)` (rename from `Source(...)`), and the out-of-roots test to `new LocalLibrarySource([_root], [], NullLogger<LocalLibrarySource>.Instance)`. Run to confirm the Inc 1 behavior survives the rename:
Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LocalLibrarySource" -v minimal`
Expected: the migrated movie tests PASS (the series tests below don't exist yet).

- [ ] **Step 4: Write the failing series-listing tests**

Append to `LocalLibrarySourceTests`. Build a `Series/Breaking Show/Season 01/` tree in the constructor OR in-test; here in-test for clarity:
```csharp
    private (string seriesRoot, string showDir) MakeShow()
    {
        var seriesRoot = Path.Combine(_root, "TV");
        var showDir = Path.Combine(seriesRoot, "Breaking Show");
        var seasonDir = Path.Combine(showDir, "Season 01");
        Directory.CreateDirectory(seasonDir);
        File.WriteAllBytes(Path.Combine(seasonDir, "Breaking.Show.S01E02.mkv"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(seasonDir, "Breaking.Show.S01E01.mkv"), new byte[] { 5, 6, 7, 8 });
        return (seriesRoot, showDir);
    }

    private LocalLibrarySource Series(string seriesRoot)
        => new([], [seriesRoot], NullLogger<LocalLibrarySource>.Instance);

    [Fact]
    public void No_series_roots_declares_no_series_catalog()
        => Assert.DoesNotContain(new LocalLibrarySource([_root], [], NullLogger<LocalLibrarySource>.Instance).Catalogs,
                                 c => c.Id == "series");

    [Fact]
    public void A_series_root_declares_the_series_catalog()
    {
        var (seriesRoot, _) = MakeShow();
        Assert.Contains(Series(seriesRoot).Catalogs, c => c.Id == "series" && c.MediaType == "series");
    }

    [Fact]
    public async Task Series_catalog_lists_each_show_as_an_expandable_item()
    {
        var (seriesRoot, _) = MakeShow();
        var catalog = await Series(seriesRoot).SearchAsync("series", null, Ctx(), default);
        var item = Assert.Single(catalog.Items);
        Assert.Equal("Breaking Show", item.Title);
        Assert.Equal("series", item.MediaType);
        Assert.True(item.Expandable);
    }

    [Fact]
    public async Task Series_query_filters_by_show_title()
    {
        var (seriesRoot, _) = MakeShow();
        Assert.Empty((await Series(seriesRoot).SearchAsync("series", "nomatch", Ctx(), default)).Items);
        Assert.Single((await Series(seriesRoot).SearchAsync("series", "breaking", Ctx(), default)).Items);
    }
```
(`Ctx()` and `_root` already exist from Inc 1's test file; keep them.)

- [ ] **Step 5: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LocalLibrarySource" -v minimal`
Expected: FAIL — no `series` catalog / `SearchAsync` doesn't handle `"series"`.

- [ ] **Step 6: Implement the `series` catalog listing + `ResolveSafeDir`**

In `LocalLibrarySource.SearchAsync`, branch on `catalogId`. Keep the existing movie logic under `catalogId == "movies"`. Add:
```csharp
    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        return catalogId switch
        {
            "movies" => Task.FromResult(ScanMovies(query, ct)),
            "series" => Task.FromResult(ListShows(query, ct)),
            _ => Task.FromResult(SourceCatalog.Empty("Local Library")),
        };
    }
```
Move the existing movie-scan body into a private `SourceCatalog ScanMovies(string? query, CancellationToken ct)` (unchanged logic). Add:
```csharp
    private static readonly EnumerationOptions TopLevelDirs = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private SourceCatalog ListShows(string? query, CancellationToken ct)
    {
        var parser = new DefaultReleaseParser();
        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var root in _seriesRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", TopLevelDirs))
            {
                ct.ThrowIfCancellationRequested();
                if (!IsContained(dir)) continue;

                var name = Path.GetFileName(dir);
                var parsed = parser.Parse(name, MediaType.Tv).NormalizedTitle;
                var title = string.IsNullOrWhiteSpace(parsed) ? name : parsed;

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(Id: EncodeId(dir), Title: title, Subtitle: string.Empty,
                    MediaType: "series", Expandable: true));
            }
            if (capped) break;
        }

        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return new SourceCatalog("Series", ordered, capped);
    }
```
Add `ResolveSafeDir` next to `ResolveSafePath` (a directory, contained in a SERIES root):
```csharp
    /// <summary>Decodes an id and confirms it is a real directory inside a configured SERIES root —
    /// gating DetailAsync so an arbitrary or foreign folder id can never be enumerated. Null for any
    /// bad id, a file, or a directory outside the series roots.</summary>
    internal string? ResolveSafeDir(string itemId)
    {
        if (TryDecodeId(itemId) is not { } decoded) return null;

        string full;
        try { full = Path.GetFullPath(decoded); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }

        if (!Directory.Exists(full)) return null;

        var resolved = ResolveReal(full);
        foreach (var root in _seriesRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string r;
            try { r = Path.GetFullPath(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { continue; }
            var resolvedRoot = ResolveReal(r);
            if (resolved.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, PathComparison) ||
                resolved.Equals(resolvedRoot, PathComparison))
                return resolved;
        }
        return null;
    }
```
(`ResolveReal`, `PathComparison`, `TryDecodeId`, `MaxItems` already exist. `ResolveReal` is `private static` — reuse it. If `ResolveSafeDir`'s root-loop duplication of `IsContained` bothers a reviewer, that's acceptable here: it enforces the SERIES-root subset, which `IsContained`'s union does not.)

- [ ] **Step 7: Run to verify pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LocalLibrarySource" -v minimal`
Expected: PASS — movie tests + series-listing tests green.

- [ ] **Step 8: Commit**

```bash
git add EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.LocalLibrary/LocalLibraryConfig.cs EverythingBox.Server.LocalLibrary/LocalLibraryPlugin.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs
git commit -m "feat: list local TV shows in a series catalog (renaming the source to LocalLibrarySource)"
```
(If `git mv` left the old filenames staged as deletes, include them in the add so the rename is recorded.)

---

### Task 2: `DetailAsync` — expand a series into ordered episodes

**Files:**
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (`DetailAsync`)
- Modify: `EverythingBox.Server.Tests/LocalLibrarySourceTests.cs` (episode tests)

**Interfaces:**
- Consumes: `ResolveSafeDir` (Task 1), `IsContained`/`WalkOptions`/`EncodeId` (existing), `DefaultReleaseParser` (Season/Episodes).

- [ ] **Step 1: Write the failing episode tests**

Append to `LocalLibrarySourceTests`:
```csharp
    private async Task<string> ShowIdAsync(string seriesRoot)
        => (await Series(seriesRoot).SearchAsync("series", null, Ctx(), default)).Items.Single().Id;

    [Fact]
    public async Task Expanding_a_show_returns_its_episodes_ordered()
    {
        var (seriesRoot, _) = MakeShow();
        var episodes = await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default);
        Assert.Equal(2, episodes.Items.Count);
        Assert.Equal("S01E01", episodes.Items[0].Title);
        Assert.Equal("S01E02", episodes.Items[1].Title);
        Assert.All(episodes.Items, e => Assert.Equal("series", e.MediaType));
        Assert.All(episodes.Items, e => Assert.False(e.Expandable));
        Assert.Contains("Breaking.Show.S01E01.mkv", episodes.Items[0].Subtitle);
    }

    [Fact]
    public async Task Non_episode_files_under_a_show_are_excluded()
    {
        var (seriesRoot, showDir) = MakeShow();
        File.WriteAllBytes(Path.Combine(showDir, "trailer.mkv"), new byte[] { 0 }); // no SxxEyy → not an episode
        var episodes = await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default);
        Assert.Equal(2, episodes.Items.Count);
    }

    [Fact]
    public async Task A_file_id_does_not_expand()
    {
        var (seriesRoot, _) = MakeShow();
        var episodeId = (await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default)).Items[0].Id;
        Assert.Empty((await Series(seriesRoot).DetailAsync(episodeId, Ctx(), default)).Items); // a file id → nothing to expand
    }

    [Fact]
    public async Task A_series_folder_id_is_not_served()
    {
        var (seriesRoot, _) = MakeShow();
        var showId = await ShowIdAsync(seriesRoot);
        Assert.Null(await Series(seriesRoot).ResolveAsync(showId, 0, Ctx(), default)); // a folder is never served
        Assert.Null(await Series(seriesRoot).OpenAsync(showId, null, default));
    }

    [Fact]
    public async Task An_episode_serves_with_range()
    {
        var (seriesRoot, _) = MakeShow();
        var episodeId = (await Series(seriesRoot).DetailAsync(await ShowIdAsync(seriesRoot), Ctx(), default)).Items[0].Id;
        await using var r = await Series(seriesRoot).OpenAsync(episodeId, "bytes=1-2", default);
        Assert.NotNull(r);
        Assert.Equal(206, r!.StatusCode);
        Assert.Equal("bytes 1-2/4", r.ContentRange);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LocalLibrarySource" -v minimal`
Expected: FAIL — `DetailAsync` still returns `Empty` for a series folder.

- [ ] **Step 3: Implement `DetailAsync` expansion**

Replace `DetailAsync` with:
```csharp
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (ResolveSafeDir(itemId) is not { } showDir)
            return Task.FromResult(SourceCatalog.Empty("Local Library"));

        var parser = new DefaultReleaseParser();
        var episodes = new List<(int Season, int Episode, CatalogItem Item)>();

        foreach (var path in Directory.EnumerateFiles(showDir, "*", WalkOptions))
        {
            ct.ThrowIfCancellationRequested();
            if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;
            if (!IsContained(path)) continue;

            var info = parser.Parse(Path.GetFileNameWithoutExtension(path), MediaType.Tv);
            if (info.Season is not { } season || info.Episodes.Count == 0) continue;
            var episode = info.Episodes[0];

            episodes.Add((season, episode, new CatalogItem(
                Id: EncodeId(path),
                Title: $"S{season:D2}E{episode:D2}",
                Subtitle: Path.GetFileName(path),
                MediaType: "series",
                Expandable: false)));
        }

        var ordered = episodes
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .Select(e => e.Item)
            .ToList();

        var title = Path.GetFileName(showDir);
        return Task.FromResult(new SourceCatalog(title, ordered));
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LocalLibrarySource" -v minimal`
Expected: PASS — expansion, ordering, exclusion, cross-shape guards, and episode serving all green.

- [ ] **Step 5: Full-suite check + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both green, incl. `RepositoryCleanlinessTests` and `SampleSourceTests`.
```bash
git add EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs
git commit -m "feat: expand a local TV show into its episodes, ordered by season and episode"
```

Optionally update `EverythingBox.Server.LocalLibrary/README.md` to mention the `Series` config and the show/season layout (stage it in this commit if you do).

---

## Self-review

**Spec coverage:**
- Rename to `LocalLibrarySource`, two catalogs, movie behavior preserved (spec Approach) → Task 1 Steps 1-3. ✅
- `LocalLibraryConfig.Series` + plugin passes both (spec Components) → Task 1 Step 2. ✅
- `series` catalog conditional; `SearchAsync("series")` lists immediate child dirs as expandable shows, titled via parser, query-filtered, ordered, capped (spec) → Task 1 Step 6. ✅
- `ResolveSafeDir` gates expansion to a directory in a series root; `IsContained` spans the union for serving (spec Security) → Task 1 Steps 1, 6. ✅
- `DetailAsync` flat episodes, `SxxEyy` title, filename subtitle, ordered by (season, episode), non-`SxxEyy` excluded, multi-ep first number (spec) → Task 2 Step 3. ✅
- Cross-shape guards (file id → no expand; folder id → not served) + episode serving with Range (spec Testing) → Task 2 Step 1. ✅
- No host/contract change, no API bump, cleanliness green (spec What binds) → Global Constraints; no task touches host/Abstractions/`ServerApi`. ✅
- `.nfo`/artwork, season tier, music, multi-ep-as-multi out of scope → no task adds them. ✅

**Placeholder scan:** none — every code step shows complete code.

**Type consistency:** `LocalLibrarySource(IReadOnlyList<string>, IReadOnlyList<string>, ILogger)` defined Task 1, used in all test helpers. `ResolveSafeDir(string) -> string?` defined Task 1 Step 6, consumed by `DetailAsync` Task 2 Step 3. `EncodeId`/`ResolveSafePath`/`IsContained`/`ResolveReal`/`WalkOptions`/`VideoExtensions`/`MaxItems` reused as named. Catalog ids `"movies"`/`"series"` and media type `"series"` consistent. Episode title format `S{season:D2}E{episode:D2}` consistent between the impl and the `"S01E01"` test assertions.
