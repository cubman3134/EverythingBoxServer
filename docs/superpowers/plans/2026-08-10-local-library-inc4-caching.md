# Local Library Plugin — Increment 4 (incremental metadata cache) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cache the per-file `.nfo` parse + poster probe by path+mtime so repeated browses of an unchanged local library skip that work — plugin-only, correctness identical to Inc 3.

**Architecture:** A `LibraryMetaCache` wraps the existing `IResolverCache` and memoizes an `ItemMeta` (raw `.nfo` fields + poster path) keyed by media+nfo mtime. `LocalLibrarySource`'s scans route their per-item parse through it; the plugin backs it with a `FileResolverCache` under its private cache dir. A null cache (unit tests) means no caching — byte-identical to Inc 3.

**Tech Stack:** .NET 9 / C#, xUnit. `System.Text.Json` for the tiny cache value. No new NuGet package (`IResolverCache`/`FileResolverCache` ship in Abstractions).

## Global Constraints

- **Plugin-only.** No `EverythingBox.Server`/Abstractions/contract change, no `ServerApi` bump, no new package.
- **Correctness identical to Inc 3.** The cache only elides recomputation; a `null` cache (the default in every existing unit test) behaves exactly as today. Any given library state returns byte-identical results cached or not.
- Cache key = `mediaPath | File.GetLastWriteTimeUtc(mediaPath).Ticks | (nfoPath is null ? "0" : nfo mtime ticks)`. `File.GetLastWriteTimeUtc` works for files AND directories and returns a stable sentinel for a missing path (never throws).
- The cache is **best-effort**: a miss/corrupt/deserialize-failure recomputes; `SetAsync` failures are swallowed; nothing throws out of `LibraryMetaCache`.
- **PUBLIC repo cleanliness:** no external content-source name; `RepositoryCleanlinessTests` stays green.
- Stage by explicit path (never `git add -A`); no AI attribution.
- No test spawns a process, touches the network, or reads a real browser profile — temp files + an in-memory cache only.
- Run tests per-project.

---

### Task 1: `LibraryMetaCache` + `ItemMeta`

**Files:**
- Create: `EverythingBox.Server.LocalLibrary/LibraryMetaCache.cs`
- Test: `EverythingBox.Server.Tests/LibraryMetaCacheTests.cs`

**Interfaces:**
- Produces: `internal sealed record ItemMeta(string? NfoTitle, int? Year, string? Plot, string? PosterPath)`; `internal sealed class LibraryMetaCache(IResolverCache? cache)` with `Task<ItemMeta> GetOrComputeAsync(string mediaPath, string? nfoPath, Func<ItemMeta> compute, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Create `EverythingBox.Server.Tests/LibraryMetaCacheTests.cs`:
```csharp
using System.Collections.Concurrent;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class LibraryMetaCacheTests : IDisposable
{
    private sealed class MemoryCache : IResolverCache
    {
        public ConcurrentDictionary<string, string> Store { get; } = new();
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Store.TryGetValue(key, out var v) ? v : null);
        public Task SetAsync(string key, string value, CancellationToken ct = default)
        { Store[key] = value; return Task.CompletedTask; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-mc-" + Guid.NewGuid().ToString("N"));
    public LibraryMetaCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private string WriteFile(string name, byte[] bytes) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, bytes); return p; }

    private static (Func<ItemMeta> compute, Func<int> count) Counting(ItemMeta value)
    {
        var n = 0;
        return (() => { n++; return value; }, () => n);
    }

    [Fact]
    public async Task Null_cache_always_computes()
    {
        var cache = new LibraryMetaCache(null);
        var (compute, count) = Counting(new ItemMeta("T", 2020, "P", null));
        var media = WriteFile("a.mkv", [1]);
        await cache.GetOrComputeAsync(media, null, compute, default);
        await cache.GetOrComputeAsync(media, null, compute, default);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task A_hit_does_not_recompute_for_an_unchanged_file()
    {
        var store = new MemoryCache();
        var cache = new LibraryMetaCache(store);
        var media = WriteFile("b.mkv", [1]);
        var (compute, count) = Counting(new ItemMeta("Title", 1999, "Plot", "/p.jpg"));

        var first = await cache.GetOrComputeAsync(media, null, compute, default);
        var second = await cache.GetOrComputeAsync(media, null, compute, default);

        Assert.Equal(1, count());               // computed once, hit second time
        Assert.Equal(first, second);
        Assert.Equal("Title", second.NfoTitle);
        Assert.Equal(1999, second.Year);
        Assert.Equal("/p.jpg", second.PosterPath);
    }

    [Fact]
    public async Task Changing_the_media_mtime_recomputes()
    {
        var cache = new LibraryMetaCache(new MemoryCache());
        var media = WriteFile("c.mkv", [1]);
        var (compute, count) = Counting(new ItemMeta("X", null, null, null));
        await cache.GetOrComputeAsync(media, null, compute, default);
        File.SetLastWriteTimeUtc(media, File.GetLastWriteTimeUtc(media).AddSeconds(5));
        await cache.GetOrComputeAsync(media, null, compute, default);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task Changing_the_nfo_mtime_recomputes_even_if_media_is_unchanged()
    {
        var cache = new LibraryMetaCache(new MemoryCache());
        var media = WriteFile("d.mkv", [1]);
        var nfo = WriteFile("d.nfo", [2]);
        var (compute, count) = Counting(new ItemMeta("X", null, null, null));
        await cache.GetOrComputeAsync(media, nfo, compute, default);
        File.SetLastWriteTimeUtc(nfo, File.GetLastWriteTimeUtc(nfo).AddSeconds(5));
        await cache.GetOrComputeAsync(media, nfo, compute, default);
        Assert.Equal(2, count());
    }

    [Fact]
    public async Task A_corrupt_cached_value_is_recomputed()
    {
        var store = new MemoryCache();
        var cache = new LibraryMetaCache(store);
        var media = WriteFile("e.mkv", [1]);
        var key = $"{media}|{File.GetLastWriteTimeUtc(media).Ticks}|0";
        store.Store[key] = "not json";
        var (compute, count) = Counting(new ItemMeta("Recovered", null, null, null));
        var result = await cache.GetOrComputeAsync(media, null, compute, default);
        Assert.Equal(1, count());
        Assert.Equal("Recovered", result.NfoTitle);
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LibraryMetaCache" -v minimal` → FAIL (types don't exist).

- [ ] **Step 3: Implement `LibraryMetaCache`**

Create `EverythingBox.Server.LocalLibrary/LibraryMetaCache.cs`:
```csharp
using System.Text.Json;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.LocalLibrary;

/// <summary>The cached result of the expensive per-item parse: the raw .nfo fields and the located
/// poster path. Callers format these into title/subtitle/panel, so one entry serves both the
/// catalog scan and the meta panel.</summary>
internal sealed record ItemMeta(string? NfoTitle, int? Year, string? Plot, string? PosterPath);

/// <summary>
/// Memoizes an <see cref="ItemMeta"/> by (media path, media mtime, nfo mtime) so an unchanged file
/// is not re-parsed on every browse. Backed by an optional <see cref="IResolverCache"/> — null
/// (unit tests) means always compute. Best-effort: any cache error recomputes; nothing throws out.
/// </summary>
internal sealed class LibraryMetaCache(IResolverCache? cache)
{
    private static readonly JsonSerializerOptions Json = new();

    public async Task<ItemMeta> GetOrComputeAsync(
        string mediaPath, string? nfoPath, Func<ItemMeta> compute, CancellationToken ct)
    {
        if (cache is null) return compute();

        var key = $"{mediaPath}|{File.GetLastWriteTimeUtc(mediaPath).Ticks}|" +
                  (nfoPath is null ? "0" : File.GetLastWriteTimeUtc(nfoPath).Ticks.ToString());

        var hit = await cache.GetAsync(key, ct).ConfigureAwait(false);
        if (hit is not null)
        {
            try
            {
                if (JsonSerializer.Deserialize<ItemMeta>(hit, Json) is { } cached)
                    return cached;
            }
            catch (JsonException) { /* corrupt entry → recompute below */ }
        }

        var computed = compute();
        try { await cache.SetAsync(key, JsonSerializer.Serialize(computed, Json), ct).ConfigureAwait(false); }
        catch { /* best-effort: a cache write must never fail a browse */ }
        return computed;
    }
}
```

- [ ] **Step 4: Run to verify pass.**

- [ ] **Step 5: Commit**
```bash
git add EverythingBox.Server.LocalLibrary/LibraryMetaCache.cs EverythingBox.Server.Tests/LibraryMetaCacheTests.cs
git commit -m "feat: add a path+mtime metadata cache for the local library"
```

---

### Task 2: Route the scans through the cache + back it with `FileResolverCache`

**Files:**
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (ctor `cache` param; route `ScanMovies`/`ListShows`/`DetailAsync`/`MetaAsync` through the cache; make them async)
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibraryPlugin.cs` (construct `FileResolverCache`, pass it)
- Test: `EverythingBox.Server.Tests/LocalLibrarySourceTests.cs` (append caching behavior tests; update the `Movies`/`Series` helpers)

**Interfaces:**
- Consumes: `LibraryMetaCache`/`ItemMeta` (Task 1), the existing `MovieNfo`/`NfoReader`/`ArtworkFinder`/`TitleFor`/`ShowTitle`/`PosterUrl`/`ResolveSafePath`/`ResolveSafeDir`.
- Produces: `LocalLibrarySource(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, IResolverCache? cache, ILogger logger)`.

**Before writing:** read the current `LocalLibrarySource.cs` `ScanMovies`, `ListShows`, `DetailAsync`, and `MetaAsync` in full — you are transforming their existing `.nfo`+poster compute to route through the cache, NOT rewriting their logic. Preserve every other behavior (containment, ordering, cap, query filter, episode `(season,episode)` parse for sorting).

- [ ] **Step 1: Write the failing tests**

Append to `LocalLibrarySourceTests`. Add a spy cache and a helper to build a cached source:
```csharp
    private sealed class SpyCache : EverythingBox.Server.Abstractions.IResolverCache
    {
        public int Gets, Sets;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _s = new();
        public Task<string?> GetAsync(string k, CancellationToken ct = default) { Gets++; return Task.FromResult(_s.TryGetValue(k, out var v) ? v : null); }
        public Task SetAsync(string k, string v, CancellationToken ct = default) { Sets++; _s[k] = v; return Task.CompletedTask; }
    }

    private LocalLibrarySource CachedMovies(EverythingBox.Server.Abstractions.IResolverCache cache, params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, [], cache, NullLogger<LocalLibrarySource>.Instance);

    [Fact]
    public async Task A_cached_source_returns_the_same_movies_as_an_uncached_one()
    {
        var mkv = Path.Combine(_root, "generic.mkv"); File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real</title><year>2011</year></movie>");
        File.WriteAllBytes(Path.Combine(_root, "generic-poster.jpg"), [9]);

        var uncached = (await Movies().SearchAsync("movies", null, Ctx(), default)).Items.Single();
        var cached = (await CachedMovies(new SpyCache()).SearchAsync("movies", null, Ctx(), default)).Items.Single();

        Assert.Equal(uncached.Title, cached.Title);
        Assert.Equal(uncached.ThumbnailUrl, cached.ThumbnailUrl);
    }

    [Fact]
    public async Task A_second_browse_of_an_unchanged_file_does_not_re_store()
    {
        var mkv = Path.Combine(_root, "generic.mkv"); File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real</title></movie>");
        var spy = new SpyCache();
        var src = CachedMovies(spy);

        await src.SearchAsync("movies", null, Ctx(), default);
        var setsAfterFirst = spy.Sets;
        await src.SearchAsync("movies", null, Ctx(), default);

        Assert.Equal(setsAfterFirst, spy.Sets); // second browse was a pure hit — no new stores
        Assert.True(spy.Gets >= 2);             // and it did consult the cache
    }
```
(The existing Inc 1–3 tests build the source via `Movies(...)`/`Series(...)` — update those helpers to pass a `null` cache: `new(roots, [], null, NullLogger<LocalLibrarySource>.Instance)`. That keeps them exercising the uncached path.)

- [ ] **Step 2: Run to verify they fail** — the `LocalLibrarySource` ctor doesn't take a cache yet (compile error).

- [ ] **Step 3: Add the ctor param + `LibraryMetaCache` field**

In `LocalLibrarySource.cs`:
```csharp
    private readonly LibraryMetaCache _meta;

    public LocalLibrarySource(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, IResolverCache? cache, ILogger logger)
    {
        _movieRoots = movieRoots;
        _seriesRoots = seriesRoots;
        _meta = new LibraryMetaCache(cache);
        _logger = logger;
    }
```
Add `using EverythingBox.Server.Abstractions;` if not already present (it is — `IResolverCache` lives there).

- [ ] **Step 4: Route `ScanMovies` through the cache (async)**

Change `SearchAsync` to await the (now async) scanners:
```csharp
    public async Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => catalogId switch
        {
            "movies" => await ScanMovies(query, ct),
            "series" => await ListShows(query, ct),
            _ => SourceCatalog.Empty("Local Library"),
        };
```
Make `ScanMovies` `private async Task<SourceCatalog>` and replace the per-file `nfo`/`title`/`ThumbnailUrl` block with a cached compute:
```csharp
                var nfoPath = MovieNfo(path);
                var meta = await _meta.GetOrComputeAsync(path, nfoPath,
                    () =>
                    {
                        var n = nfoPath is null ? null : NfoReader.TryRead(nfoPath);
                        return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(path));
                    }, ct).ConfigureAwait(false);

                var title = meta.NfoTitle is { } nt
                    ? (meta.Year is { } ny ? $"{nt} ({ny})" : nt)
                    : TitleFor(parser, path);

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(
                    Id: EncodeId(path), Title: title,
                    Subtitle: Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
                    MediaType: "movie", ThumbnailUrl: PosterUrl(meta.PosterPath), Expandable: false));
```

- [ ] **Step 5: Route `ListShows`, `DetailAsync`, `MetaAsync` through the cache (same pattern)**

Apply the identical transform:
- **`ListShows`** → `private async Task<SourceCatalog>`. For each show `dir`: `nfoPath = Path.Combine(dir, "tvshow.nfo")` (pass `File.Exists(nfoPath) ? nfoPath : null` as the key's nfoPath); `meta = await _meta.GetOrComputeAsync(dir, existsNfo, () => { var n = existsNfo is null ? null : NfoReader.TryRead(existsNfo); return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(dir)); }, ct)`; `title = meta.NfoTitle ?? ShowTitle(parser, dir)`; `ThumbnailUrl = PosterUrl(meta.PosterPath)`.
- **`DetailAsync`** → keep the fast `DefaultReleaseParser` filename parse for `(season, episode)` sorting/skip uncached; route ONLY the episode `.nfo` read through the cache: `var epNfoPath = Path.ChangeExtension(path, ".nfo"); var existsEpNfo = File.Exists(epNfoPath) ? epNfoPath : null; var meta = await _meta.GetOrComputeAsync(path, existsEpNfo, () => { var n = existsEpNfo is null ? null : NfoReader.TryRead(existsEpNfo); return new ItemMeta(n?.Title, n?.Year, n?.Plot, null); }, ct);` then `Title = $"S{season:D2}E{episode:D2}"` + (`meta.NfoTitle is {} en ? $" - {en}" : "")`. `DetailAsync` becomes `async` (it already returns `Task`).
- **`MetaAsync`** → route both branches through the cache. File branch: `nfoPath = MovieNfo(file)`; `meta = await _meta.GetOrComputeAsync(file, nfoPath, () => { var n = nfoPath is null ? null : NfoReader.TryRead(nfoPath); return new ItemMeta(n?.Title, n?.Year, n?.Plot, ArtworkFinder.PosterFor(file)); }, ct)`; build `SourceDetail(Title: meta.NfoTitle ?? TitleFor(new DefaultReleaseParser(), file), Overview: meta.Plot, ImageUrl: PosterUrl(meta.PosterPath), Facts: meta.Year is { } y ? [new MetaFact("Year", y.ToString())] : [])`. Dir branch: same with `tvshow.nfo` + `ShowTitle`. `MetaAsync` becomes `async`; return `null` for an invalid id exactly as before (the `ResolveSafePath`/`ResolveSafeDir` gate is unchanged and runs BEFORE any cache/NFO work).

Preserve: containment gates run first; ordering, cap, query filter, and the `(season,episode)` sort are unchanged; a null cache path yields identical output.

- [ ] **Step 6: Back the cache in the plugin**

In `LocalLibraryPlugin.Configure`:
```csharp
        var cache = new FileResolverCache(Path.Combine(context.CacheDirectory, "meta"), 16L * 1024 * 1024);
        registry.AddSource(new LocalLibrarySource(config.Movies, config.Series, cache, context.Loggers.CreateLogger<LocalLibrarySource>()));
```
(Add `using EverythingBox.Server.Abstractions;` if needed for `FileResolverCache`.)

- [ ] **Step 7: Full suites + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both green — the Inc 1–3 `LocalLibrarySource` tests (now null-cache) unchanged, the new caching tests pass, `RepositoryCleanlinessTests` + `SampleSourceTests` green.
```bash
git add EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.LocalLibrary/LocalLibraryPlugin.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs
git commit -m "feat: cache local-library .nfo/artwork parses by path+mtime across browses"
```
(Optionally note the cache in the plugin `README.md`; stage it if you do.)

---

## Self-review

**Spec coverage:** `LibraryMetaCache`/`ItemMeta` keyed by media+nfo mtime, best-effort, null=compute (spec) → Task 1. Ctor `cache` param (null = identical Inc 3), scans routed through the cache, plugin backs it with a 16 MB `FileResolverCache` under the cache dir (spec) → Task 2. Correctness-identical + hit-skips-recompute tested (spec Testing) → Task 1 Steps 1, Task 2 Step 1. Music + directory-index out of scope → no task adds them. No host/contract change, no API bump → no task touches host/Abstractions/`ServerApi`. ✅

**Placeholder scan:** none — `LibraryMetaCache` shown in full; the Task 2 wire-in gives the exact cached-compute closure for each of the four call sites and preserves all other behavior (transform, not rewrite).

**Type consistency:** `ItemMeta(NfoTitle, Year, Plot, PosterPath)` + `LibraryMetaCache.GetOrComputeAsync(string, string?, Func<ItemMeta>, CancellationToken)` produced in Task 1, consumed identically in all four Task 2 call sites. `LocalLibrarySource(IReadOnlyList<string>, IReadOnlyList<string>, IResolverCache?, ILogger)` defined Task 2 Step 3, used by the plugin (Step 6) and every test helper. `MetaFact`/`SourceDetail` (from Inc 3) used unchanged in `MetaAsync`.
