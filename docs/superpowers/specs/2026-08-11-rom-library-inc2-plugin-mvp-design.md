# ROM library (EBS#12) — Increment 2: the RomLibrary plugin MVP

**Status:** approved 2026-08-11, ready for planning.

## Where this fits

EBS#12 is a ROM-library plugin, a sibling to #8's LocalLibrary. Increment 1 (merged, API 1.14)
extracted the shared local-file substrate (`SafeLocalFileServer` — containment + Range serving + id
minting; generic `LibraryMetaCache`) into Abstractions. **This increment builds the plugin itself**:
scan console folders, expose per-system `game` catalogs, and serve ROMs with Range — reusing the
Increment-1 primitive so no security code is rewritten.

Increment 3 (later) adds `gamelist.xml` + local boxart + a meta panel.

## The binding client contract (why the structure is what it is)

Verified against the EverythingBox client (Project Goliath):

- The client renders whatever catalogs the manifest advertises; a `game`-type catalog is a games shelf.
- **`systemHint` — which emulator/core the client loads — is derived ONLY from a parent
  `type:"platform"` item's *title*, matched through the client's `forConsoleName`.** It is NOT read
  from a game item's JSON, the stream id, or the catalog. So the plugin MUST expose each console as a
  `platform` container whose title is a recognizable console name; the ROMs are its `game` children.
- Catalog/detail `game` items carry no playable `url`; the client fetches `/stream/game/{id}` on open.
- The stream response is `{url,mime}` (or `{streams:[…]}`) with a **direct http(s)** url. The client
  keeps the ROM extension from the **url path** (else from an `application/x-<ext>` mime), caches the
  file, and hands it to the emulator chosen by `systemHint`. `.zip/.7z/.rar` are accepted + auto-extracted.
- `game` is a native built-in client media type — browse → download → play already works. No client change.

## Architecture

A new in-repo plugin project `EverythingBox.Server.RomLibrary` (source key `"romlib"`), mirroring
LocalLibrary's structure and load model (in-repo like SampleSource/LocalLibrary: `Private="false"`
Abstractions ref, referenced only by the Tests project, NOT by `EverythingBox.Server`, loaded from
`plugins/romlib/`). Fresh-checkout-serves-nothing holds — empty `Catalogs` when no roots configured.

**One source, `RomLibrarySource`, implementing `IMediaSource`:**

- **`Catalogs`** → a single `SourceCatalog` `{ Id="games", Name="Games", MediaType="game" }` when at least
  one root is configured (else empty).
- **`SearchAsync("games", …)`** → one `CatalogItem` per detected **system folder** across the roots:
  - `MediaType = "platform"`, `Expandable = true`.
  - `Title` = the console **display title** from the embedded system table (a `forConsoleName`-recognizable
    name); an unrecognized folder falls back to the folder name.
  - `Id` = `SafeLocalFileServer.EncodeId(<absolute system-folder path>)`.
  - `Subtitle` = the game count (e.g. "42 games"), best-effort.
  - Deduplicate/merge by resolved system id when the same system appears under multiple roots? **No for
    MVP** — one platform item per folder (simple, honest); multi-root merge is a later polish. Sorted by title.
- **`DetailAsync("platform", id)`** → the ROMs directly under that system folder as `CatalogItem`s:
  - `MediaType = "game"`, `Expandable = false`.
  - `Title` = filename stem (extension removed); the raw stem, unprettified (Inc 3's gamelist supplies
    real names). `Subtitle` = filename. `ThumbnailUrl` = null (Inc 3 adds boxart).
  - `Id` = `SafeLocalFileServer.EncodeId(<absolute ROM file path>)`.
  - Files are every non-junk file directly in the folder (a small junk filter: skip dotfiles, `.txt`,
    `.nfo`, `.xml`, `.dat`, `.jpg/.png` art, `.m3u` for MVP). Sorted by title. A reasonable cap (e.g. 5000).
  - The folder id is gated by `ResolveSafeDir` against the ROM roots (a foreign/file id → empty).
- **`ResolveAsync("game", id)`** → a `SourceStream` whose `Url` is the plugin's proxy URL
  `proxy/{Key}/{itemId}/{fileName}` (fileName carries the extension) and `Mime = application/x-<ext>`,
  gated by `ResolveSafeFile`. Null for a non-contained/foreign id.
- **`OpenAsync(itemId, rangeHeader, ct)`** → `_files.OpenAsync(itemId, rangeHeader, ct)` — the
  Increment-1 Range server, verbatim. ROMs seek/resume for free.
- **`MetaAsync`** → null for MVP (no meta panel until Inc 3).

**Two `SafeLocalFileServer` instances** (same discipline as LocalLibrary):
- `_files = new SafeLocalFileServer(roots, MimeFor)` — file serving/resolution over the ROM roots.
- `_dirs  = new SafeLocalFileServer(roots, MimeFor)` — `ResolveSafeDir` for the platform→games drill.
  (Both over the same root set here — ROMs have one root class, unlike LocalLibrary's movies/series —
  but kept as two calls for parity/clarity; a single instance used for both would be equivalent.)

`MimeFor` maps a ROM extension to `application/x-<ext>` (lowercased, no dot), and the handful of art
extensions to their image types (unused until Inc 3). Content type is belt-and-suspenders: the client
primarily takes the extension from the proxy url path, which already carries the filename.

## System mapping table (the one unavoidable duplication)

The public repo cannot reference the client's `SystemCatalog`, so the plugin embeds a compact, data-only
`RomSystems` table: **folder name (lowercased) → (systemId, consoleTitle)**. It covers the common
systems using the RetroBat/ES-DE folder spellings the client already aliases, and titles them with
`forConsoleName`-recognizable names. Examples (illustrative, not exhaustive — the plan enumerates the set):

| folder aliases | systemId | consoleTitle (must be forConsoleName-recognizable) |
|---|---|---|
| `nes`, `famicom` | `nes` | `Nintendo Entertainment System` |
| `snes`, `superfamicom`, `sfc` | `snes` | `Super Nintendo` |
| `genesis`, `megadrive`, `mastersystem`, `gamegear` | `genesis` | `Sega Genesis` |
| `n64` | `n64` | `Nintendo 64` |
| `psx`, `ps1`, `playstation` | `psx` | `Sony PlayStation` |
| `gba` | `gba` | `Game Boy Advance` |
| `gb`, `gbc` | `gb` | `Game Boy` |
| `gamecube`, `gc` | `gc` | `Nintendo GameCube` |
| `psp` | `psp` | `Sony PSP` |
| `nds`, `ds` | `nds` | `Nintendo DS` |
| `dreamcast`, `dc` | `dreamcast` | `Sega Dreamcast` |
| `saturn` | `saturn` | `Sega Saturn` |
| `pce`, `pcengine`, `tg16` | `pce` | `PC Engine` |

Resolution order for a scanned folder: exact-id match → alias match → **fall back to the folder name as
the title** (still lists + serves; the client may not auto-pick a core, which is acceptable and honest).
`systemId` is not sent to the client (there is no channel for it); it exists so the title is stable and
for Inc 3 use. **The console title is the contract-critical field** — it is the only `systemHint` channel.

## Config

`RomLibraryConfig` — a single `Roms` list of **library root** paths. Each immediate subfolder of a root
is treated as a system folder (resolved via the table; folder name as title otherwise). Matches the
standard RetroBat/ES-DE layout. Empty/absent → the source contributes no catalog (fresh-checkout safe).
`RomLibraryPlugin.Configure` deserializes it, constructs `RomLibrarySource(roots, cache?, logger)`
(cache reserved for Inc 3; may be omitted in the ctor for MVP if unused).

## Host API

**No API bump.** Increment 2 uses only existing contract surface: `IMediaSource`, `SourceCatalog`,
`CatalogItem` (with `MediaType` = "platform"/"game", `Expandable`), `SourceStream`, `ProxyResponse`,
the existing routes (`manifest`/`catalog`/`detail`/`stream`/`proxy`). `platform` and `game` are just
`MediaType` string values the client already understands. Engine stays at 1.14.

## Testing

- **`RomLibrarySourceTests`** (new, in `EverythingBox.Server.Tests`), over temp dirs:
  - No roots → empty `Catalogs`.
  - A root with `snes/` + `psx/` subfolders → the `games` catalog lists two `platform` items with the
    mapped console titles, expandable, ids that round-trip.
  - An unrecognized folder (e.g. `weirdbox/`) → still listed, titled by folder name.
  - `DetailAsync` on a platform id → the ROM files as `game` items, junk files (`.txt`/`.xml`/dotfiles)
    excluded, sorted, ids round-trip; title = filename stem.
  - `DetailAsync` on a foreign/file id → empty (containment).
  - `ResolveAsync` on a game id → a `SourceStream` with the `proxy/romlib/…/<name.ext>` url and
    `application/x-<ext>` mime; a foreign id → null.
  - `OpenAsync` serves the ROM with Range (206/200/416) — reuses the Inc-1 server; one smoke test of
    a 206 slice + a full 200 suffices (the Range engine is already covered by `SafeLocalFileServerTests`).
  - Path traversal: a `game`/`platform` id decoding outside the roots serves/lists nothing.
- **`RomSystemsTests`** — folder alias → (id, title) resolution; unknown folder → null (caller falls back).
- **Manifest/registry** — the plugin registers, advertises the `games` catalog, key `"romlib"`.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- **Correct `systemHint` depends on the `platform` item title being `forConsoleName`-recognizable** —
  this is the contract-critical invariant; the system table's titles are chosen for it and tested.
- All containment/Range serving is the Increment-1 `SafeLocalFileServer`, reused verbatim — no new
  security code; a light review confirms the plugin only ever resolves ids through it.
- In-repo plugin, not referenced by the engine, loaded from `plugins/romlib/` — fresh-checkout serves
  nothing.
- **Cleanliness:** no external content-source name; `RepositoryCleanlinessTests` stays green. (ROMs are
  a generic local-file concept — no source name involved.)
- No new NuGet package. No API bump.

## Out of scope (Inc 3 or later / deferred)

- `gamelist.xml`, local boxart, the game meta panel (Inc 3).
- Multi-disc `.m3u` grouping (client-generated), ROM hashing, No-Intro/DAT verification, scraping.
- Multi-root merge of the same system into one platform item (MVP shows one per folder).
- Prettified titles (MVP uses the raw filename stem; Inc 3's gamelist supplies real names).
- Archive-internal browsing (the client auto-extracts `.zip/.7z/.rar` on its side).

## Done when

- `EverythingBox.Server.RomLibrary` (`"romlib"`) scans configured roots, advertises a `games` catalog of
  `platform` items titled with recognizable console names, drills each into its ROM `game` items, and
  serves them with Range through the Increment-1 `SafeLocalFileServer`.
- Browse platform → games → download/play works against the real client contract (verified by the
  catalog/detail/stream shapes the tests assert).
- Both engine test projects + the plugin tests green including `RepositoryCleanlinessTests`; engine
  stays at API 1.14. Increment 3 (gamelist + art + meta) can build on this.
