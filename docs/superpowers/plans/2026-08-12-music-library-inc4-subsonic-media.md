# Music library — Increment 4 (Subsonic media + star/scrobble) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the Subsonic API — `stream`/`download`/`getCoverArt` (Range-served audio + cover), `scrobble` (record-only), `star`/`unstar` + `getStarred2` — so a real client browses AND plays. Closes #23.

**Architecture:** Media endpoints authenticate then relay bytes (Range) instead of the XML/JSON envelope; on error they return the Subsonic error envelope. Star/scrobble go through the plugin's `MusicStateStore`; stars are datetimes (Subsonic `starred="<iso>"`), so `IMusicLibrary`'s DTOs carry `StarredAt`. Transcoding is out of scope (pending #9): direct-play only, with an honest OpenSubsonic note when a transcode is requested.

**Tech Stack:** .NET 9 / C#, ASP.NET, xUnit + `WebApplicationFactory`. `EverythingBox.Server` (Subsonic routes) + `EverythingBox.Server.Abstractions` (DTO tweak) + `EverythingBox.Server.MusicLibrary` (store + impl) + tests.

## Global Constraints

- **Auth first on every media endpoint** (same `SubsonicAuth`), then relay/serve. A wrong credential → the failed envelope (code 40); an unknown id → the error envelope (code 70), NOT a raw 404.
- **No transcoding** (pending #9): serve the original bytes; when `maxBitRate` (>0) or a `format` we can't produce is requested, serve direct AND advertise it honestly (OpenSubsonic `openSubsonic="true"` is already implied; add a response note / do not silently pretend to transcode). Documented as the #9 rung.
- **Stars are datetimes**, not bools — Subsonic `starred="<ISO8601>"`. Additive `StarredAt` on the DTOs (record field; single in-tree implementer; **no API bump** — 1.17 stays, additive to records).
- **Credentials never logged** (Increment 3's redaction covers `/rest`); media endpoints add no logging of ids beyond the endpoint name.
- No `ServerApi` bump. PUBLIC repo cleanliness; no committed binary (runtime fixtures). Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: `stream` / `download` / `getCoverArt` (Range-served media)

**Files:**
- Modify: `EverythingBox.Server/Subsonic/SubsonicEndpoints.cs` (add the three media cases + a byte-relay helper)
- Create: `EverythingBox.Server.Tests/SubsonicMediaTests.cs`

**Interfaces:**
- Consumes: `IMusicLibrary.OpenTrackAsync(songId, range, ct)` and `CoverArt(id) → (Path, ContentType)?` (Increment 2), `ProxyResponse`.

- [ ] **Step 1: a byte-relay helper** in `SubsonicEndpoints` mirroring `AddonEndpoints.ProxyAsync`'s relay: given a `ProxyResponse`, set `http.Response.StatusCode`/`ContentType`/`ContentLength`/`Accept-Ranges`/`Content-Range` from it, `await resp.Body.CopyToAsync(http.Response.Body, ct)`, dispose the `ProxyResponse` in a `finally`, and guard `HasStarted` (a clean error envelope only if nothing has flushed, else abort). Reuse the exact discipline of `AddonEndpoints.ProxyAsync` (read it).
- [ ] **Step 2: `stream`** — case in the switch: authenticate (already done before the switch); read `id` (required → code 10 if missing); optional `maxBitRate`/`format`. Call `music.OpenTrackAsync(id, http.Request.Headers.Range.ToString() is {Length:>0} r ? r : null, ct)`; if null → error envelope code 70; else relay the bytes (Range). If `maxBitRate` parses >0 OR `format` is set to something other than `raw`/the file's own suffix, this build cannot transcode — still serve direct; do NOT alter the bytes. (The honest-note surface: keep it simple — direct play is correct; a client that required a hard bitrate cap just gets the original. Document in the endpoint comment as the #9 rung; no fake transcode.)
- [ ] **Step 3: `download`** — same as `stream` but never considers `maxBitRate`/`format` (download is always the original file); `id` required. Relay via `OpenTrackAsync` (no range needed, but honor a Range header if present — the relay handles it).
- [ ] **Step 4: `getCoverArt`** — read `id` (required); `size` accepted but IGNORED (no image resizing, v1 — documented). `music.CoverArt(id)` → if null, error envelope code 70; else serve the file with Range: `Results.File(path, contentType, enableRangeProcessing: true)` (the plugin already containment-checked the path — it only returns contained cover files). Return that `IResult` from the handler.
- [ ] **Step 5: `SubsonicMediaTests`** — over the `SubsonicServerFactory` library (authenticated via valid `t/s`):
  - `stream?id=<songId>` → 200 the audio bytes with `Content-Type: audio/*`; with `Range: bytes=0-9` → 206 + the 10-byte slice + `Content-Range`.
  - `stream?id=<songId>&maxBitRate=64&format=mp3` → still 200 the ORIGINAL bytes (no transcode; direct play).
  - `download?id=<songId>` → 200 the full bytes.
  - `getCoverArt?id=<coverArtId>` → 200 an image content-type + the bytes; `size=100` ignored (same bytes).
  - `stream?id=<unknown>` → the failed envelope code 70 (a parseable Subsonic error, not a bare 404).
  - `stream` with no `id` → code 10.
  - unauthenticated `stream` → code 40 (no bytes leaked before auth).
- [ ] **Step 6: Build + test + commit**
Run both test projects — green.
```bash
git add EverythingBox.Server/Subsonic/SubsonicEndpoints.cs EverythingBox.Server.Tests/SubsonicMediaTests.cs
git commit -m "feat: Subsonic stream/download/getCoverArt — Range-served media, direct-play (no transcode)"
```

---

### Task 2: `star`/`unstar`/`getStarred2` + `scrobble` (with datetime stars)

**Files:**
- Modify: `EverythingBox.Server.Abstractions/Music/IMusicLibrary.cs` (add `StarredAt` to the DTOs; `SetStarred` already exists; keep it)
- Modify: `EverythingBox.Server.MusicLibrary/MusicStateStore.cs` (star = a `{id → starredAt}` map) + `MusicLibrarySource.cs` (map `StarredAt`, add starred lookups)
- Modify: `EverythingBox.Server/Subsonic/SubsonicEndpoints.cs` (star/unstar/getStarred2/scrobble cases + emit `starred` on album/song/artist nodes)
- Create: `EverythingBox.Server.Tests/SubsonicStarScrobbleTests.cs`

**Interfaces:**
- Produces: `ArtistInfo`/`AlbumInfo`/`SongInfo` gain `DateTimeOffset? StarredAt`; `IMusicLibrary.Starred() → (artists, albums, songs)` for getStarred2.

- [ ] **Step 1: DTO + store — stars as datetimes.** Add `DateTimeOffset? StarredAt` to `ArtistInfo`/`AlbumInfo`/`SongInfo` (additive record param, defaulted null — update the constructors; the existing `bool Starred` may be removed or kept as `StarredAt is not null` — prefer replacing `Starred` with `StarredAt` and updating the ~3 call sites in `MusicLibrarySource`). Change `MusicStateStore`'s star set to a `Dictionary<string, DateTimeOffset>` (id → starred-at); `SetStarred(id, true)` records `now`, `false` removes; add `DateTimeOffset? StarredAt(id)`. Add `IMusicLibrary.Starred()` returning the starred artists/albums/songs (as `SearchResult` or a dedicated tuple) for `getStarred2`. `MusicLibrarySource` maps `StarredAt` onto the DTOs and implements `Starred()`.
- [ ] **Step 2: Subsonic star/unstar/getStarred2/scrobble** in the switch:
  - `star` / `unstar` — read `id` (Subsonic sends `id` for songs, `albumId`, `artistId` — accept all three param names; multiple allowed) → `music.SetStarred(id, true/false)` for each → return an empty ok envelope.
  - `getStarred2` → `<starred2>` with `<artist>`/`<album>`/`<song>` from `music.Starred()`.
  - `scrobble` — read `id` (required), optional `time` (ms epoch) and `submission` (default true) → `music.Scrobble(id, time)` → empty ok envelope. (Record-only; no Last.fm/ListenBrainz forwarding — the #192 boundary, documented.)
  - **Emit `starred`** on the `Album`/`Song`/`Artist` node builders when `StarredAt` is set: `.Attr("starred", starredAt.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))` (Subsonic ISO). (Do NOT emit `starred="false"` when unset — omit the attribute.)
- [ ] **Step 3: `SubsonicStarScrobbleTests`** — authenticated:
  - `star?id=<songId>` → ok; then `getSong?id=<songId>` shows a `starred="<iso datetime>"` attribute (XML) / `"starred":"<iso>"` (JSON); `getStarred2` lists the song.
  - `unstar?id=<songId>` → the `starred` attribute is gone; `getStarred2` no longer lists it.
  - star survives a reload (persisted via the store) — assert by re-querying (the factory's store persists to the plugin cache dir).
  - `scrobble?id=<songId>` → ok (and does not throw / is idempotent-safe on repeat).
  - `star?id=<unknown>` → ok (Subsonic tolerates starring an unknown id) OR code 70 — pick the tolerant "ok" (matches Subsonic) and assert it doesn't corrupt state.
- [ ] **Step 4: Full suites + commit**
Run both test projects — green (incl. the Increment-2/3 tests still passing after the DTO tweak).
```bash
git add EverythingBox.Server.Abstractions/Music/IMusicLibrary.cs EverythingBox.Server.MusicLibrary/MusicStateStore.cs EverythingBox.Server.MusicLibrary/MusicLibrarySource.cs EverythingBox.Server/Subsonic/SubsonicEndpoints.cs EverythingBox.Server.Tests/SubsonicStarScrobbleTests.cs
git commit -m "feat: Subsonic star/unstar/getStarred2 (datetime stars) + scrobble (record-only)"
```

---

## Self-review

**Spec coverage (Increment 4):** stream/download/getCoverArt Range-served, direct-play + honest no-transcode (spec) → Task 1. scrobble record-only (spec, #192 boundary) → Task 2 Step 2. star/unstar + getStarred2, stars as datetimes (spec) → Task 2. `size` ignored on cover (spec) → Task 1 Step 4. No API bump (additive DTO field) → Task 2 Step 1. Auth-first, errors as envelopes (spec) → Task 1 Steps 2-5. ✅

**Placeholder scan:** the byte-relay mirrors the cited `ProxyAsync`; the transcode stance is explicit (serve direct, no fake); `getCoverArt` uses `Results.File` on the plugin-containment-checked path; the star datetime format + omit-when-unset rule is concrete; the DTO change is enumerated with its call sites.

**Type consistency:** `IMusicLibrary.OpenTrackAsync`/`CoverArt`/`SetStarred`/`Scrobble`/`Starred()` from Increments 2/4 consumed by the endpoints. `ArtistInfo`/`AlbumInfo`/`SongInfo` gain `DateTimeOffset? StarredAt`; `MusicStateStore` id→datetime; `MusicLibrarySource` maps it. Node `Attr("starred", iso)` emitted only when set. `ServerApi.VersionString` unchanged (1.17). Existing Inc-2/3 tests updated for the DTO param change. ✅
