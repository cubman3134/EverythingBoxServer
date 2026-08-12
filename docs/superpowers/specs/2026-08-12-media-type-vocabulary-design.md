# Media-type vocabularies (EBS#4): one bridge, one rename, one documented model

**Status:** approved 2026-08-12, ready for planning.

## What this is

Issue #4 asked for a `MediaTypeNames` helper bridging the `MediaType` enum and the catalog protocol
strings, and to force the "music can't be expressed" gap into the open. **Most of that already shipped**
(milestone 2b): `MediaTypeNames` exists in Abstractions with `ToProtocolString`/`TryParseProtocol`, maps
`Music ↔ "music"`, and is tested; `IndexerSearchSource` already surfaces a `"music"` catalog. So this
issue is now the *remaining* rough edges, scoped deliberately (the request-factory centralization the
issue hints at is explicitly **out** — see Out of scope):

1. The **CS0117 name collision** (real, unaddressed): the `MediaType` enum and the string members
   `CatalogDescriptor.MediaType` / `CatalogItem.MediaType` share the `EverythingBox.Server.Abstractions`
   namespace, so any class in that namespace declaring a same-named member turns `MediaType.Tv` into a
   confusing CS0117.
2. **Magic protocol strings** scattered at call sites (`"series"`/`"movie"` hard-coded ~6 places in
   `MetadataBackedVideoSource`; `"game"`/`"platform"`/`"games"` in `RomLibrary`) instead of one source.
3. **Music is expressible but not canonical** — it works via `MediaTypeNames` + `IndexerSearchSource`,
   but it is missing from the authoritative protocol-string list documented on `Catalog.cs`, so the
   deliberate decision ("music is a first-class search-backed catalog type") was never written down.
4. **No written model** for how the vocabularies relate, so the next person re-guesses.

## The model we are committing to (and documenting)

There are two *legitimately different* vocabularies plus one presentation vocabulary — not three that
must merge:

- **`MediaType` enum** — the **pipeline** vocabulary (torznab category, request subclass, ranker rules).
  Members: `Movie, Tv, Music, Audiobook, Book, Comic, Other, PcGame`.
- **Protocol strings** — the **client** vocabulary carried on `CatalogDescriptor`/`CatalogItem`:
  `"movie", "series", "comic", "manga", "book", "audiobook", "music", "game", "platform"`.
- **`MediaTypeDescriptor`** — presentation hints (color/icon/layout) for a protocol type the client does
  **not** know natively. Orthogonal; unchanged by this work.

`MediaTypeNames` bridges the **intersection** of the first two. The non-overlap is intentional and gets
documented rather than "fixed":

| Protocol string | enum via `FromProtocol` | note |
|---|---|---|
| `movie` | `Movie` | |
| `series` | `Tv` | name differs by design |
| `comic`, `manga` | `Comic` | many-to-one; reverse picks `comic` |
| `book` | `Book` | |
| `audiobook` | `Audiobook` | |
| `music` | `Music` | first-class **both** sides |
| `game` | `PcGame` | client says "a game"; the enum's PC-vs-console split is a pipeline-only concern |
| `platform` | *(none)* | **client-only** container type (a console shelf); `TryParseProtocol("platform") == false` is CORRECT — RomLibrary serves it directly, never through the pipeline |
| *(none)* | `Other` | **pipeline-only** fallback; deliberately absent from `ToProtocol` |

`"game"` and `"platform"` are **client-native** media types (the client already renders them — `game`
is a built-in type, `platform` renders as a drillable shelf), exactly like `movie`/`series`. So, like
those, they need **no** `MediaTypeDescriptor`. (This is why the earlier "add descriptors for game/platform"
idea is dropped — it would contradict "descriptors are for types the client does NOT know natively.")

## Changes

### 1. Rename the colliding member: `CatalogDescriptor.MediaType` / `CatalogItem.MediaType` → `Kind`
Purely internal — the catalog **wire shape is hand-projected**, not serialized from these records:
`ManifestBuilder.cs:64` emits `new { id, name, type = c.MediaType }` and `AddonEndpoints.cs:396` emits
`type = i.MediaType`. The JSON key stays the literal `type`; only the C# reads change to `c.Kind`/`i.Kind`.
No `JsonPropertyName` needed; **no client-wire change**. `MediaTypeDescriptor.Type` (a different member
name) is untouched.

The sweep updates: the two wire projections; every `.MediaType` read on a catalog descriptor/item; and
every **named-argument** construction `new CatalogItem(… MediaType: "…" …)` / `CatalogDescriptor(…)` →
`Kind:` (positional constructions like `new CatalogDescriptor("games","Games","game")` are unaffected).
Spans the engine + the three in-repo plugins (LocalLibrary, RomLibrary, SampleSource) **and the private
downstream plugin in lockstep** (any place it builds or reads a catalog item's type). The `MediaType` enum
keeps its name.

### 2. Protocol-string constants on `MediaTypeNames`
Add `public const string` for every protocol string — `Movie="movie"`, `Series="series"`, `Comic="comic"`,
`Manga="manga"`, `Book="book"`, `Audiobook="audiobook"`, `Music="music"`, `Game="game"`, `Platform="platform"`
— and use them to replace the hard-coded literals in `MetadataBackedVideoSource` (`"series"`/`"movie"` at
the ~6 sites) and `RomLibrary` (`"game"`/`"platform"`; the `"games"` catalog **id** is not a protocol type
and stays a literal). The `ToProtocol`/`FromProtocol` dictionaries are rewritten to reference these consts
(single source of truth). Behavior identical; the strings simply have one home now.

### 3. Make music canonical + write the model down
Update the `Catalog.cs` doc comment on `CatalogDescriptor`/`CatalogItem` to list the full, authoritative
protocol set **including `"music"`, `"game"`, `"platform"`**. Put the vocabulary model (the table above:
enum = pipeline, strings = client, `MediaTypeNames` bridges the intersection, and the three non-overlap
cases) as a doc comment on `MediaTypeNames`, so it lives next to the code that owns it.

## Host API

The member rename is a **source-breaking** change to the public `CatalogDescriptor`/`CatalogItem` contract
(an out-of-tree plugin reading `.MediaType` would no longer compile), even though the wire is unchanged.
Bump `ServerApi.VersionString` **1.14 → 1.15** and update the two version-pin tests (+ the compat theory
gains `[InlineData(1, 14)]`). All in-repo plugins + the private plugin are recompiled against 1.15 in the same change.
The constants and doc changes are additive and covered by the same bump.

## Testing

- **`MediaTypeNamesTests`** (extend) — the new consts equal their string values; `ToProtocol`/`FromProtocol`
  still round-trip every enum member except `Other`; `manga`→`Comic` one-way; **`TryParseProtocol("platform")`
  returns false and this is asserted as intentional** (a regression guard for the client-only case);
  `ToProtocolString(MediaType.Other)` is null.
- **Wire-stability test** — assert the manifest catalog JSON and the catalog-route item JSON still emit the
  key `"type"` with the right value after the rename (a `CatalogDescriptor`/`CatalogItem` with `Kind:"series"`
  serializes to `type:"series"` through the actual projection). This is the guard that the internal rename
  did not leak to the wire.
- **`MetadataBackedVideoSource` / `RomLibrary` suites** — unchanged assertions; they must pass after the
  literals become consts and the member is renamed (behavior-preserving).
- Version-pin tests → 1.15; compat theory gains `[InlineData(1, 14)]`.
- The whole engine + all in-repo plugin test projects + the private plugin's tests compile and pass.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- **No wire change** — the catalog `type` key and every protocol-string VALUE are identical; only C#
  member names and the location of string literals change. The wire-stability test is the gate.
- **CS0117 gone** — after the rename, no member named `MediaType` exists in the Abstractions namespace,
  so `MediaType.Tv` always binds to the enum.
- **One source of truth** — every protocol string is a `MediaTypeNames` const; the vocabulary model is
  one doc comment; music is in the canonical list.
- **Cross-repo lockstep** — the public repo and the private plugin is updated and green together; the API bump
  (1.15) is the compat signal.
- **Cleanliness** — no external content-source name; `RepositoryCleanlinessTests` green. No new package.

## Out of scope (deliberately)

- **Centralizing the enum→`MediaRequest`-subclass maps** (`IndexerSearchSource.BuildRequest`,
  `MetadataBackedVideoSource.ResolveAsync`) into a factory — the user chose the foundational scope; those
  are pipeline-internal, already work, and are not the vocabulary confusion this issue is about.
- **Renaming the `MediaType` enum** (the other CS0117 option) — rejected in favor of the smaller,
  wire-neutral member rename.
- **New enum members** (e.g. a console-game type) — console games are client-only protocol types by
  design; the pipeline never routes them.
- Any client-side change — the client wire is untouched.

## Done when

- `CatalogDescriptor`/`CatalogItem` expose `Kind` (not `MediaType`); CS0117 cannot recur; the wire still
  emits `type`; the wire-stability test proves it.
- Every protocol string is a `MediaTypeNames` const; the `"series"`/`"movie"`/`"game"`/`"platform"`
  literals are gone from call sites; the vocabulary model + full protocol list are documented on
  `MediaTypeNames`/`Catalog.cs`, music included.
- Engine at API **1.15**; the engine, all in-repo plugin tests, and the private plugin all compile and pass, including
  `RepositoryCleanlinessTests`. #4 can be closed.
