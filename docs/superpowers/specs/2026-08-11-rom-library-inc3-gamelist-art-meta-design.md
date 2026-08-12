# ROM library (EBS#12) — Increment 3: gamelist.xml names, boxart, and the game meta panel

**Status:** approved 2026-08-11, ready for planning.

## Where this fits

EBS#12's final planned increment. Inc 1 extracted the shared local-file primitive; Inc 2 shipped the
`RomLibrary` plugin (`"romlib"`) — a `games` catalog of `platform` containers drilling into ROM `game`
items served with Range. This increment gives those games **real names, boxart, and a meta panel**,
reading each system folder's `gamelist.xml` (the ES-DE / RetroBat standard). Plugin-only, mirroring how
#8's Increment 3 added `.nfo` + artwork to LocalLibrary.

## No host API bump

#8's Increment 3 already shipped the meta contract at the current engine version (1.14): `SourceDetail`
+ `MetaFact` DTOs, the default `IMediaSource.MetaAsync` (default null → unchanged for sources that don't
implement it), and the type-agnostic `/meta/{type}/{id}` route that serializes the flat client shape
`{title,subtitle,overview,image,facts:[{label,value}]}` via `ToWireMeta` (dropping empty-value facts).
RomLibrary Inc 3 **implements** `MetaAsync` and sets `ThumbnailUrl`; it adds **no new host surface**.
Boxart serves through the **existing** `OpenAsync`/proxy path (a boxart file is a contained file —
`ResolveSafeFile` gates it, `MimeFor` already maps jpg/png/webp). Engine stays at **API 1.14**.

## What the plugin gains

### 1. `GamelistStore` — parse a system folder's `gamelist.xml`
XXE-safe exactly like `NfoReader`: `XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null,
MaxCharactersFromEntities = 1024 }`, `XmlReader.Create(stream, settings)` → `XDocument.Load` (NEVER
`XDocument.Load(path)`), catch-all → an empty index (a malformed gamelist must degrade to "no metadata",
never throw out of a browse). Reads the top-level `<gameList>` and indexes each `<game>` by the
**filename** of its `<path>` (relative `./Game (USA).sfc` → key `Path.GetFileName`, case-insensitive) —
matching MVP's top-level-files layout. Fields read per game:
- `<name>` → title
- `<desc>` → overview (plain text)
- `<image>` (preferred) / `<thumbnail>` (fallback) → boxart path (relative to the system folder)
- `<releasedate>` (ISO `yyyyMMddThhmmss`) → year (first 4 chars)
- `<developer>`, `<publisher>`, `<genre>`, `<players>` → facts

The store is built once per system folder per browse and memoized (see cache); a folder with no
`gamelist.xml` yields an empty index and every game falls back to Inc-2 behavior.

### 2. Titles
A game's title becomes its gamelist `<name>` when present, else the Inc-2 filename stem. Platform
(console) titles are unchanged — still the `RomSystems` console name.

### 3. Boxart — gamelist first, then sibling-file discovery
Resolve a game's boxart in this order, taking the first that resolves to a **contained, existing file**:
1. **gamelist** `<image>` then `<thumbnail>`, resolved relative to the system folder.
2. **sibling discovery** (`RomArtFinder`, modeled on #8's `ArtworkFinder`): `<stem>-image.*`,
   `images/<stem>.*`, `media/covers/<stem>.*`, `media/images/<stem>.*` (ES-DE/RetroBat conventions),
   then a folder-level `boxart.*` / `folder.*`. Extensions: jpg/jpeg/png/webp.

Every candidate path is validated through `_files.ResolveSafeFile` before it is turned into a
`proxy/romlib/{EncodeId(artPath)}/{filename}` URL — a gamelist `<image>` pointing outside the roots
(e.g. `../../etc/passwd`) resolves to null and is skipped, never served. Set the resolved URL as the
game item's `ThumbnailUrl` and the meta panel's `ImageUrl`. Platform items keep `ThumbnailUrl = null`
(console logos are themed client-side, like #8 skipped backgrounds) — art is a game-level concern here.

### 4. `MetaAsync(game id)`
Resolve the game file via `_files.ResolveSafeFile`; look up its gamelist entry + boxart; return
`SourceDetail(Title: name??stem, Overview: desc, ImageUrl: boxartUrl, Facts: [Year, Developer,
Publisher, Genre, Players])` — empty-value facts are dropped by the existing `ToWireMeta`. A game with
no metadata returns a `SourceDetail` with just its stem title (honest, non-null panel). A non-game/foreign
id → null (unchanged).

### 5. Cache — reuse the generic `LibraryMetaCache`, close the type-discriminator carry
`RomLibraryPlugin.Configure` builds `FileResolverCache(Path.Combine(context.CacheDirectory, "meta"),
16 MB)` and passes it to `RomLibrarySource` (new `IResolverCache? cache` ctor param, null in unit tests
= no caching = Inc-2-identical, exactly as #8 Inc 4 did). A `GameMeta` record (Name, Desc, Year,
Developer, Publisher, Genre, Players, ArtPath — the raw parse + located art, so ONE entry serves both
the catalog scan and the meta panel) is memoized via `_meta.GetOrComputeAsync<GameMeta>(romPath,
gamelistPath, compute, ct)` — the shared file is the `gamelist.xml` (so an edited gamelist invalidates
every game in that folder via the mtime component). **Type-discriminator hardening (the Minor carried
since Inc 1):** `LibraryMetaCache` includes `typeof(T).FullName` in its cache key, so a ROM `GameMeta`
and a video `ItemMeta` for the same path can never deserialize into each other. This is a small,
additive, behavior-only change to the shared key composition (no signature/API change); it invalidates
existing LocalLibrary cache entries once (best-effort → they recompute), which is harmless.

## LocalLibrary parity note

The `LibraryMetaCache` key change is the only shared-code edit. LocalLibrary's full test suite must stay
green (the cache is best-effort; a one-time recompute is invisible to behavior). No LocalLibrary source
change.

## Testing

- **`GamelistStoreTests`** — parse a small `gamelist.xml`: name/desc/image/releasedate→year/developer/
  genre/players read; `<path>` filename indexing (with and without `./`); a missing gamelist → empty
  index; **XXE**: a gamelist with an external-entity / DOCTYPE payload does NOT resolve it and returns
  empty (mirror `NfoReaderTests`' XXE case); a malformed XML → empty, no throw.
- **`RomArtFinderTests`** — sibling discovery order over temp dirs (`<stem>-image`, `images/<stem>`,
  `media/covers/<stem>`, `boxart`), first-match wins; no art → null.
- **`RomLibrarySourceTests`** (extend) — with a `gamelist.xml`: a game's title is the `<name>`; its
  `ThumbnailUrl` is a `proxy/romlib/…` URL for the resolved boxart; `MetaAsync` returns overview +
  boxart + the expected facts; a gamelist `<image>` pointing outside the roots is NOT served (null art,
  no traversal); a game absent from the gamelist falls back to the stem title and sibling art; boxart
  serves 200 through `OpenAsync`. Existing Inc-2 assertions unchanged where no gamelist is present.
- **`LibraryMetaCacheTests`** (extend) — two different `T`s for the same (path, mtimes) do not collide
  (the type-discriminator case); existing hit/miss/mtime/corrupt/null cases unchanged.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- **No API bump** — reuses the meta contract #8 shipped; engine stays 1.14.
- **No new serving/containment code** — boxart rides the Inc-1 `SafeLocalFileServer` `OpenAsync`/
  `ResolveSafeFile`; every art path (including gamelist-referenced ones) is containment-checked before
  it becomes a URL. A light security review confirms no gamelist `<image>` can escape the roots and no
  XXE in `GamelistStore`.
- **Behavior-preserving where no gamelist exists** — Inc-2 filename-stem titles + no art remain the
  fallback; a folder without `gamelist.xml` browses exactly as in Inc 2.
- **Cleanliness** — no external content-source name; `RepositoryCleanlinessTests` stays green.
- No new NuGet package.

## Out of scope (deferred)

- Video / marquee / fanart art roles (client themes them; not base-resolved — same call #8 made for
  backgrounds).
- Multi-disc `.m3u` grouping, ROM hashing / No-Intro DAT verification, online scraping (all still #12-out).
- Platform (console) logos/art.
- Nested-subfolder ROM layouts (MVP + this increment index top-level files; gamelist `<path>` with a
  subfolder still resolves by filename but nested enumeration is a later concern).

## Done when

- Each system folder's `gamelist.xml` supplies real game names, boxart, and a populated meta panel;
  games without an entry fall back to Inc-2 behavior; boxart (gamelist-referenced or sibling-discovered)
  serves through the existing proxy/Range path, always containment-checked.
- `GamelistStore` is XXE-safe; the generic `LibraryMetaCache` carries a type discriminator; LocalLibrary
  is untouched and green.
- Both engine test projects + the plugin tests green including `RepositoryCleanlinessTests`; engine
  stays at API 1.14. **EBS#12 can then be closed** (all three increments delivered).
