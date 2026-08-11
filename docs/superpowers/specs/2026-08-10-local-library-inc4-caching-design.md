# Local Library plugin — Increment 4: incremental metadata cache

**Status:** approved 2026-08-10, ready for planning.

## Where this fits

EBS#8, increment 4 of 4 — the finish. Increments 1–3 (movies + Range, series, `.nfo` + artwork)
are merged. Inc 3 made every browse parse an `.nfo` (XML) and probe up to ~12 poster candidates
**per file**, on every catalog/search hit. This increment caches that per-file work by
path + mtime so repeated browses of an unchanged library skip it. Plugin-only; no host/contract
change, no API bump. **Music is deferred** — a separate, lower-value follow-up (see Out of scope).

## Goal

A large local library browses fast: the second and subsequent scans of unchanged files reuse a
cached parse instead of re-reading `.nfo` XML and re-probing artwork. Editing an `.nfo` (or the
media file) invalidates just that entry. Correctness is identical to Inc 3 — the cache only skips
recomputation, never changes what a browse returns.

## Approach — a per-file parse cache over the existing `IResolverCache`

The expensive per-file work is: read the sidecar `.nfo` (`NfoReader.TryRead`) and find a poster
(`ArtworkFinder.PosterFor`). Its result is a tiny record. Cache that, keyed by the file's identity
+ modification time, using the BCL-only `FileResolverCache` (a size-bounded, best-effort, on-disk
LRU already in Abstractions, reachable from a plugin) under the plugin's private cache directory.

### `LibraryMetaCache` (new, `EverythingBox.Server.LocalLibrary`)

```csharp
/// <summary>The cached result of the expensive per-item parse: the raw .nfo fields and the located
/// poster path. Callers format these into a title/subtitle/panel; storing the RAW parse lets both
/// the catalog scan and the meta panel share one cache entry.</summary>
internal sealed record ItemMeta(string? NfoTitle, int? Year, string? Plot, string? PosterPath);

internal sealed class LibraryMetaCache(IResolverCache? cache)
{
    /// <summary>Returns the cached <see cref="ItemMeta"/> for (<paramref name="mediaPath"/>,
    /// <paramref name="nfoPath"/>) at their current mtimes, or runs <paramref name="compute"/> and
    /// stores it. A null cache (tests) always computes. A corrupt/missing entry recomputes.</summary>
    public async Task<ItemMeta> GetOrComputeAsync(
        string mediaPath, string? nfoPath, Func<ItemMeta> compute, CancellationToken ct)
    {
        if (cache is null) return compute();

        // File.GetLastWriteTimeUtc works for files AND directories and returns a stable sentinel
        // (never throws) for a missing path — so the key is race-tolerant.
        var key = $"{mediaPath}|{File.GetLastWriteTimeUtc(mediaPath).Ticks}|" +
                  (nfoPath is null ? "0" : File.GetLastWriteTimeUtc(nfoPath).Ticks.ToString());

        var hit = await cache.GetAsync(key, ct).ConfigureAwait(false);
        if (hit is not null && TryDeserialize(hit, out var cached))
            return cached;

        var computed = compute();
        await cache.SetAsync(key, Serialize(computed), ct).ConfigureAwait(false); // best-effort
        return computed;
    }
}
```
- Key = `mediaPath | mediaMtimeTicks | nfoMtimeTicks` (nfo ticks `0` when there is no sidecar).
  Statting the two mtimes is cheap; what a hit skips is the XML parse + the ~12-probe poster search.
- Editing the `.nfo` changes `nfoMtimeTicks` → the entry misses → re-parse. Replacing the media
  file changes `mediaMtimeTicks` → re-parse.
- **Known staleness (documented, acceptable for a cache):** adding/removing a poster *without*
  touching the media file or its `.nfo` won't refresh `PosterPath` until one of them changes or the
  entry is LRU-evicted. Worst case: a missing/added thumbnail until the next real change. No
  correctness impact on playback.
- `Serialize`/`TryDeserialize` use `System.Text.Json` on `ItemMeta`; a deserialize failure (format
  drift, truncation) → recompute (never throw out of the cache).

### Wiring into `LocalLibrarySource`

- Constructor gains an `IResolverCache? cache` parameter → wrapped in a `LibraryMetaCache _meta`.
  **Null (unit tests) means no caching — behavior byte-identical to Inc 3.**
- `LocalLibraryPlugin.Configure` builds `new FileResolverCache(Path.Combine(context.CacheDirectory, "meta"), 16L * 1024 * 1024)` and passes it in. (16 MB holds ~100k tiny entries; a constant for now — a config knob is a later YAGNI.)
- `ScanMovies`, `ListShows`, `DetailAsync`, and `MetaAsync` route their per-item `.nfo`+poster work
  through `_meta.GetOrComputeAsync(mediaPath, nfoPath, compute, ct)`, where `compute` is the current
  Inc 3 logic (`NfoReader.TryRead` + `ArtworkFinder.PosterFor`), and each caller formats `ItemMeta`
  into its display fields:
  - **Movie row/meta:** `nfoPath = MovieNfo(file)`; `Title = ItemMeta.NfoTitle` (+ `(Year)`) else the
    parser filename title; `ThumbnailUrl`/`ImageUrl = PosterUrl(ItemMeta.PosterPath)`; `MetaAsync`
    adds `Overview = Plot`, `Facts = [Year]`.
  - **Show row/meta:** `mediaPath = dir`, `nfoPath = <dir>/tvshow.nfo`; `Title = NfoTitle ?? ShowTitle`;
    poster from the folder.
  - **Episode:** the fast filename parse still yields `(season, episode)` for sorting/skip
    (uncached — it is cheap); the cache covers only the episode `.nfo` read: `Title = "SxxEyy"` +
    (`- {NfoTitle}` when present).
  - Because these now `await` per file, `ScanMovies`/`ListShows`/`DetailAsync`/`MetaAsync` become
    genuinely `async` (they already return `Task`; drop the `Task.FromResult` wrappers). The query
    filter still runs on the computed title (a cache hit still yields the title cheaply).
- No security, serving, containment, or contract code changes — this is purely a compute cache in
  front of the existing parse/probe.

## Data flow

First browse of a file: cache miss → parse `.nfo` + probe poster → store → format. Later browse,
file unchanged: cache hit → format (no XML, no poster probe). File or `.nfo` changed: key differs →
miss → re-parse. Cache full / entry evicted: miss → re-parse (best-effort, self-healing).

## Testing

- **`LibraryMetaCache`** with an in-memory `IResolverCache` fake (a `Dictionary`-backed stub) and a
  counting `compute`:
  - a miss runs `compute` once and stores; a second call for the same file (same mtime) is a **hit**
    — `compute` is NOT called again (assert the counter);
  - touching the media file's mtime (rewrite it) → key changes → `compute` runs again;
  - touching the `.nfo`'s mtime (with the media unchanged) → `compute` runs again;
  - a corrupt cached value (store garbage under the computed key) → `compute` runs (recovers);
  - a **null** cache always runs `compute` and never touches storage.
- **Wire-in over a temp dir** (extend `LocalLibrarySourceTests`): a `LocalLibrarySource` built WITH
  an in-memory cache returns the same titles/posters/episode-titles/meta as the Inc 3 (null-cache)
  path (assert equality against a null-cache source over the same tree); and two successive
  `SearchAsync` calls parse each file's `.nfo` at most once (assert via a counting `IResolverCache`
  or a spy — a second browse issues gets, not recomputes). Existing Inc 1–3 tests (which build the
  source with a null cache) still pass unchanged after the ctor gains the optional parameter.
- No test spawns a process, touches the network, or reads a real browser profile — temp files +
  an in-memory cache only.

## What binds

- Plugin-only: no `EverythingBox.Server`/Abstractions/contract change, no `ServerApi` bump, no new
  NuGet package (`FileResolverCache`/`IResolverCache` already ship in Abstractions).
- Correctness is identical to Inc 3 for any given library state — the cache only elides
  recomputation; a stale entry is bounded by mtime changes and LRU eviction, and never affects
  playback or containment.
- The optional-`cache` ctor keeps every existing unit test valid (null → no caching).
- Cleanliness: names no external content source; `RepositoryCleanlinessTests` stays green.
- Fresh-checkout-serves-nothing unaffected.

## Out of scope

- **Music** (a `music` catalog + audio roots) — deferred as a separate, lower-value follow-up:
  without ID3 tags it is filename-only (like movies pre-`.nfo`); with tags it needs a tag-reader
  dependency. Noted on #8, not built here.
- A persistent **directory index** that avoids the tree *walk* itself (only re-walking dirs whose
  mtime changed). The per-file parse cache captures the dominant cost; walk-avoidance is a further
  optimization, YAGNI for now.
- Any change to serving/containment/meta contract; watch state, transcoding, archive browsing.

## Done when

- A second browse of an unchanged library reuses cached parses — proven by a test that `compute`
  runs at most once per file across two `SearchAsync` calls — while returning byte-identical
  results to the uncached path; editing an `.nfo` or the media file re-parses just that file.
- `LibraryMetaCache` is unit-tested (hit/miss/mtime-change/corrupt/null); the plugin builds its
  `FileResolverCache` under the cache dir; no host/contract change, no API bump; all engine +
  plugin tests green including `RepositoryCleanlinessTests`. Verified in Release. **This completes
  EBS#8's movies/TV/metadata scope; #8 can close with music noted as a deferred follow-up.**
