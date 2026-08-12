# Game updates & DLC — recognize, group, serve (EBS#22)

**Status:** approved 2026-08-12, ready for planning.

## Goal

A console game is base + update(s) + DLC — separate files. Today #12's RomLibrary lists them as three
unrelated `game` entries. This is the **server half**: identify each file's title identity, group
base + update + DLC of one title into one entry, and serve each member. Installing a set into an
emulator is the client's job (EverythingBox#189) and out of scope here. Extends the RomLibrary; PUBLIC
repo; built in two increments.

## Identification (settled: filename convention + PS3 headers)

`TitleIdentifier.Identify(path) → PackageIdentity?` where `PackageIdentity(string TitleId, TitleKind
Kind, int? Version)`, `TitleKind ∈ { Base, Update, Dlc }`.

- **Filename / release-naming convention (all platforms)** — the "near-universal" path the issue leans on:
  - **Switch**: a 16-hex title id in the name. Base = the id (typically ending `…000`); its **update** is
    the same program id ending `…800`; **DLC** occupy `base + 0x1000 …` (the low 12 bits nonzero). Group
    key = the base id (the 16-hex with its low 12 bits zeroed). Version from a `[v65536]`/`[vNNNNN]` marker.
  - **Wii U / 3DS**: title id in the name with the type bits (`0005000E` update, `0005000C` DLC vs
    `00050000` base for Wii U; 3DS `.cia` similar) → base id + kind.
  - **PS3**: game code (`BLES…`/`BLUS…`/`NPUB…`/`NPEB…`) in the name; base vs update vs DLC by
    keyword (`update`/`patch`/`vX.YZ` → update; `dlc` → dlc). Group key = the game code.
  - **Generic fallback**: `update`/`patch`/`dlc`/`vN.NN` markers grouped by a normalized title stem.
- **PS3 PKG header + `PARAM.SFO` (real binary parsing)** — where the content id lives and naming is least
  reliable: open the `.pkg`, verify the `\x7FPKG` magic, locate the embedded `PARAM.SFO`, parse the SFO
  (header magic `\0PSF`, index table, key/data tables) → `TITLE_ID`, `CATEGORY` (`HG` = base game vs
  `GD`/patch vs DLC category), `APP_VER`/`VERSION`. The parsed identity **overrides** the filename guess
  for `.pkg`. XXE/oversize-safe (bounded reads; a malformed pkg → null, never throws).
- **"Group nothing" is the safe default.** When identity is ambiguous or missing, the file is its own
  singleton entry — a wrong grouping is worse than none (the issue's rule).

`TitleGrouper.Group(files) → IReadOnlyList<TitleGroup>` where `TitleGroup(BaseTitleId, BasePath,
IReadOnlyList<(Path,Version)> Updates, IReadOnlyList<string> Dlc)`. A group forms **only around a present
Base**; an orphan update/DLC with no base in the same folder stays its own entry (never invent a base).
Updates sorted by version (newest flagged); pure, no I/O beyond the identify step's header read.

## Increment 1 — the identification + grouping engine (pure, testable)

New files in `EverythingBox.Server.RomLibrary`: `PackageIdentity`/`TitleKind`, `TitleIdentifier`
(filename rules + `Ps3PkgReader` for PKG/PARAM.SFO), `TitleGrouper`. All internal, BCL-only (binary
reads via `BinaryReader`/`Span`; no NuGet). Tests synthesize inputs at runtime — Switch/WiiU/3DS/PS3
**named** files (empty content is fine for the naming path) and a **minimal valid PS3 `.pkg`** (a small
in-code byte template: `\x7FPKG` header + an embedded `PARAM.SFO` with `TITLE_ID`/`CATEGORY`/`APP_VER`)
— so nothing binary is committed and `RepositoryCleanlinessTests` stays green. Assert: Switch base+…800
update+…1000 DLC group under one base; a PS3 pkg's identity comes from PARAM.SFO (overriding the name);
an orphan update with no base stays separate; an unidentifiable file is its own singleton; version
ordering flags the newest update.

## Increment 2 — wire grouping into the RomLibrary shelf

- **`RomLibraryConfig.GroupUpdatesAndDlc`** (bool, default `true`). When false, behaviour is exactly
  today's flat listing (base-only-per-file — the issue's configurable opt-out).
- **`DetailAsync(platform-dir id)`** — run `TitleGrouper` over the folder's ROM files. Emit ONE `game`
  item per group headed by a base: a group WITH members → `Expandable = true`, `Subtitle` = e.g.
  "1 update · 2 DLC"; a singleton/ungrouped game → `Expandable = false` (a direct-play leaf, exactly as
  today). Ungrouped files and gamelist titles/art are preserved. Grouping reuses the platform→game
  drill mechanism — **no new contract surface** (the client-side "one tile, pull the set" presentation
  is #189).
- **`DetailAsync(base-game id)`** — a NEW branch: if the id resolves (`ResolveSafeFile`) to a file that
  is a group **base** within its folder, re-group that folder, find its group, and return
  `{ base, update(s) newest-first, dlc }` as `game` items with the role + version in the title/subtitle
  (e.g. "Update v65536", "DLC — Expansion"), each individually streamable. A non-base game id → empty
  (a plain game has nothing to expand — unchanged).
- **`ResolveAsync`/`OpenAsync`** unchanged — every member (base/update/DLC) streams by its own file id
  through the existing proxy/Range path. Containment unchanged.
- **Tests**: a system folder with a base + a named update + a DLC (+ a synthesized PS3 pkg group) →
  `DetailAsync(platform)` shows ONE expandable base with the right member count; `DetailAsync(base id)`
  → the members with roles, each with a working stream id; `GroupUpdatesAndDlc=false` → the flat
  today-listing; an ungrouped/odd-named file stays a separate leaf; a base with no members is NOT
  expandable.

## What binds

- **Server-only, contract-minimal.** Grouping rides the existing platform→game→(expand) drill; no host
  API bump, no client dependency. Client set-install is #189; #16 request-queue keep-the-set and #97
  attachment hashing are noted integration points, not built here.
- **Identity over filename**, but filename is the near-universal fallback; PS3 pkg parsing overrides the
  name. **Group nothing when unsure.**
- **No committed binaries** — PS3 pkg + PARAM.SFO fixtures synthesized at runtime; cleanliness green.
- **No keys/firmware, no vendor update servers, no applying** — the issue's hard exclusions. We parse
  only unencrypted structure; Switch decisive metadata is encrypted, so Switch relies on the naming
  convention (stated honestly).
- BCL-only (binary parsing needs no NuGet); Core untouched (this is plugin code). Cleanliness green.

## Out of scope (per the issue)

- Shipping/fetching console keys or firmware; querying vendor update servers (Sony `…-ver.xml`); applying
  anything (serve only).
- Full binary parsing of NSP/XCI/CIA metadata (Switch CNMT is encrypted anyway) — naming convention covers it.
- The client "one tile, pull the whole set" UX (#189) and the #16 request-queue keep-the-set wiring.

## Done when

- `TitleIdentifier`/`TitleGrouper` identify base/update/DLC by naming convention (Switch/Wii U/3DS/PS3) and
  by PS3 PKG/`PARAM.SFO`, group only around a present base, and "group nothing" when unsure — unit-tested.
- The RomLibrary shelf collapses a title's files into one expandable base game whose drill lists the
  members (each streamable); `GroupUpdatesAndDlc=false` restores the flat listing; ungrouped files stay
  separate leaves.
- Both engine test projects + the plugin tests green including `RepositoryCleanlinessTests`; no committed
  binary; no API bump. #22 can be closed (client set-install tracked as #189).
