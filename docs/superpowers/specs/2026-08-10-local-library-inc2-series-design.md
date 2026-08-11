# Local Library plugin — Increment 2: series/TV

**Status:** approved 2026-08-10, ready for planning.

## Where this fits

EBS#8, increment 2 of 4 on `EverythingBox.Server.LocalLibrary`. Increment 1 (movies + Range +
path security) is merged. This increment adds TV series: a `series` catalog, series→episodes
expansion, and episode playback — reusing Inc 1's serving and security verbatim. `.nfo`/artwork
(Inc 3) and incremental/music (Inc 4) remain out of scope.

## Goal

A user configures series folders (`Show/Season NN/Show.SxxEyy.ext` convention). The plugin lists
each show as an expandable `series` item; expanding it returns a flat, ordered list of episode
items (`SxxEyy - <file>`); playing an episode serves it with Range, exactly like a movie. Same
2-level shape `MetadataBackedVideoSource` emits, so clients render local and remote series
identically.

## Approach — evolve the one source into two catalogs (approved)

Rename `MovieLibrarySource` → `LocalLibrarySource`, holding both movie and series roots and
exposing both catalogs. A local library is one thing with two shelves — the same "one source,
multiple catalogs" model `MetadataBackedVideoSource` uses. This reuses the path-security and
`OpenAsync`/Range serving directly (no duplication across two sources); containment simply spans
the union of all configured roots.

### No id tagging needed (the key simplification)

- A **file** id (movie OR episode) decodes to a file path → served by `OpenAsync`/`ResolveAsync`
  (validated by `ResolveSafePath`, which already rejects a folder via its `File.Exists` gate).
- A **series** id decodes to a *folder* path → expanded by `DetailAsync` (recognized via
  `Directory.Exists` + a series-root containment check).

So Inc 1's movie ids are **unchanged**, episode ids are just file ids, and series ids are folder
ids — distinguished purely by file-vs-directory, no type prefix.

## Components (all in `EverythingBox.Server.LocalLibrary`, no host/contract change)

### `LocalLibraryConfig` — add `Series`
```csharp
public sealed class LocalLibraryConfig
{
    public List<string> Movies { get; set; } = [];
    public List<string> Series { get; set; } = [];   // NEW
}
```

### `LocalLibraryPlugin` — pass both root lists
```csharp
registry.AddSource(new LocalLibrarySource(config.Movies, config.Series, context.Loggers.CreateLogger<LocalLibrarySource>()));
```

### `LocalLibrarySource` (renamed from `MovieLibrarySource`)

- Constructor `(IReadOnlyList<string> movieRoots, IReadOnlyList<string> seriesRoots, ILogger logger)`.
- **`Catalogs`** — conditional, each shelf only when it has roots:
  `movieRoots.Count > 0` → `("movies","Movies","movie")`; `seriesRoots.Count > 0` →
  `("series","Series","series")`. Empty when neither is configured (fresh-checkout-serves-nothing).
- **`SearchAsync(catalogId, query, …)`** branches on `catalogId`:
  - `"movies"` → the existing movie scan (unchanged behavior).
  - `"series"` → list each **immediate child directory** of each series root as a series item:
    enumerate `Directory.EnumerateDirectories(root, "*", TopDirectoryOnly)` with the same
    reparse-point-skipping options; skip a dir failing the containment backstop; title via
    `DefaultReleaseParser(dirName, MediaType.Tv).NormalizedTitle` (fallback to the folder name);
    `Id = EncodeId(folderPath)`; `MediaType = "series"`; **`Expandable = true`**; filter by `query`
    on the title; order by title; cap at `MaxItems`. (Loose files directly under a series root are
    not series and are ignored in this increment.)
  - An unknown `catalogId` → `SourceCatalog.Empty`.
- **`DetailAsync(itemId, …)`** — expand a series folder into its episodes:
  - `ResolveSafeDir(itemId)` (below) → the real, contained series folder, or null → `Empty`.
  - Enumerate the folder's video files recursively (the existing `WalkOptions`), each confirmed
    `IsContained`; parse `DefaultReleaseParser(stem, MediaType.Tv)`; keep only files that yield a
    `Season` AND at least one `Episode`.
  - Emit a flat episode `CatalogItem` per file: `Title = $"S{season:D2}E{episode:D2}"` (the first
    episode number for a multi-episode file), `Subtitle = Path.GetFileName(path)` (the real file —
    a real episode title arrives with `.nfo` in Inc 3), `Id = EncodeId(filePath)`,
    `MediaType = "series"`, `Expandable = false`.
  - Order by `(Season, Episode)` ascending. Return `new SourceCatalog(seriesTitle, episodes)`.
  - A file id (movie/episode) or any non-series-folder id → `Empty` (nothing to expand).
- **`ResolveAsync` / `OpenAsync`** — unchanged from Inc 1: serve a file id with Range. The only
  change is that containment now spans movie + series roots.

### Security helpers — containment spans all roots

- `IsContained(path)` (files) now iterates the **union** of `movieRoots` and `seriesRoots` (keep
  the two lists as fields; combine only inside the containment loop). Every existing property —
  real-resolve of junctions/symlinks, trailing-separator boundary, OS-appropriate case comparison
  — is unchanged.
- **`ResolveSafeDir(itemId) -> string?`** (NEW, mirrors `ResolveSafePath` for a directory): decode
  → `Path.GetFullPath` (catching the same narrow exceptions) → `Directory.Exists` → confirm the
  real-resolved path is contained within a **series** root (a series folder must live under a
  series root, not a movie root). Returns null for any bad/foreign/non-directory id. This gates
  `DetailAsync` so an arbitrary or out-of-roots folder id can never be enumerated.
- `EncodeId`/`TryDecodeId`/`ResolveSafePath`/`ResolveReal` are reused verbatim.

## Data flow

1. Client browses `catalog/locallib:series.json` → `SearchAsync("series", null)` → show items
   (`Expandable`).
2. Client opens a show → `detail/series/locallib:<folderId>.json` → `DetailAsync` → flat episode
   items.
3. Client plays an episode → `stream` → `ResolveAsync(<fileId>)` → a `proxy/locallib/<fileId>/…`
   URL → `OpenAsync` serves it with Range (Inc 1 path).

## Testing (extend `MovieLibrarySourceTests`, renamed to `LocalLibrarySourceTests`)

- **Movie behavior preserved:** the Inc 1 tests pass unchanged after the rename (the source still
  scans movies, serves with Range, enforces containment).
- **Series listing:** a temp tree `Series/Breaking Show/Season 01/Breaking.Show.S01E01.mkv` +
  `…S01E02.mkv` → the `series` catalog lists one item titled `Breaking Show`, `Expandable == true`,
  `MediaType == "series"`; `Catalogs` includes `series` only when series roots are configured.
- **Episode expansion:** `DetailAsync(showId)` returns two episode items ordered `S01E01`, `S01E02`,
  each `MediaType "series"`, non-expandable, `Subtitle` = the filename; a file with no `SxxEyy`
  under the show folder is excluded; a multi-episode file `S01E03E04` lists once titled `S01E03`.
- **Cross-shape guards:** `DetailAsync` on a movie/episode **file** id → `Empty`; `ResolveAsync`/
  `OpenAsync` on a **series folder** id → null (a folder is never served — `File.Exists` is false).
- **Containment:** an episode id whose path escapes all roots → `ResolveAsync`/`OpenAsync` null; a
  folder id outside the series roots → `DetailAsync` `Empty`.
- **Episode serving:** `OpenAsync(episodeId, "bytes=1-2")` → 206 + correct slice (reuses Inc 1's
  proven path).
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- Plugin-only: no host/Abstractions/contract change, no `ServerApi` version bump, no new package.
- The rename `MovieLibrarySource → LocalLibrarySource` preserves all Inc 1 behavior; movie ids are
  byte-identical; the security code is reused, not rewritten.
- Fresh-checkout-serves-nothing still holds (empty `Catalogs` when no roots; loaded only from
  `plugins/locallib/`).
- Cleanliness: names no external content source; `RepositoryCleanlinessTests` stays green.
- Episode/series shape matches `MetadataBackedVideoSource` (series `Expandable`; flat episodes,
  season in the title), so clients render local and remote series the same way.

## Out of scope

- `.nfo` sidecars + local artwork, including real episode titles (Inc 3 — episode `Subtitle` is the
  filename until then).
- A season-grouping tier (net-new 3-level shape; the host precedent is 2-level flat).
- Incremental mtime rescan + music (Inc 4).
- Multi-episode files as multiple items (they list once at their first episode number).
- Watch state, transcoding, archive browsing (the issue's exclusions).

## Done when

- With configured `Series` roots staged as `plugins/locallib/`, the server lists each show as an
  expandable `series` item; expanding one returns its episodes ordered `SxxEyy`; playing an episode
  serves it with working Range; movies (Inc 1) are unchanged.
- A folder id is never served and a file id never expands; containment rejects out-of-roots ids on
  both the serve and the expand path.
- `LocalLibrarySource` (renamed) + `ResolveSafeDir` are unit-tested over temp trees; no host/contract
  change; both engine test projects + the plugin tests green including `RepositoryCleanlinessTests`.
  Verified in Release.
