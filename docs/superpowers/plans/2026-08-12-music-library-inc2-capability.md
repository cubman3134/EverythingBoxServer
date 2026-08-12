# Music library — Increment 2 (the IMusicLibrary capability) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A host-reachable `IMusicLibrary` capability — the Subsonic-shaped query + mutate surface the coming `/rest` routes need — registered through the plugin registry the same way indexers/metadata are, and implemented by `MusicLibrarySource`. API 1.16 → 1.17.

**Architecture:** The capability registry is closed (four hardcoded methods). Extend the chain: `IMusicLibrary` + `AddMusicLibrary` in Abstractions → a field on `PluginRegistry` → a field on the `LoadedPlugin` record + `PluginHost` → read back in `Program.cs` and register the one `IMusicLibrary` in DI for the routes. `MusicLibrarySource` implements `IMusicLibrary` by mapping its internal `MusicIndex` to Abstractions DTOs, plus a small local JSON store for stars/scrobbles/playlists.

**Tech Stack:** .NET 9 / C#, xUnit. `EverythingBox.Server.Abstractions` + `EverythingBox.Server` + `EverythingBox.Server.MusicLibrary` + tests.

## Global Constraints

- **Additive contract.** New interface + one registry method + DTOs. Older plugins still load (compat check: same major, minor ≤ host). API bump 1.16 → 1.17.
- **At-most-one `IMusicLibrary`** across the server (like `ProviderTracker`): `AddMusicLibrary` throws on a second registration; `PluginRegistry` does NOT probe the instance (plugin-authored code can throw — mirror the existing "deliberately does NOT read …" discipline).
- **The DTOs live in Abstractions** (the host reads them), distinct from the plugin's internal `MusicIndex` records. Plain records, Subsonic-shaped fields.
- ATL dep stays on the plugin only; Core BCL-only tests stay green. PUBLIC repo cleanliness. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: `IMusicLibrary` + DTOs (Abstractions) + registration chain + API 1.17

**Files:**
- Create: `EverythingBox.Server.Abstractions/Music/IMusicLibrary.cs` (interface + DTOs)
- Modify: `EverythingBox.Server.Abstractions/IPlugin.cs` (add `AddMusicLibrary`)
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` (1.16 → 1.17)
- Modify: `EverythingBox.Server/Plugins/PluginRegistry.cs` (field + prop + method), `EverythingBox.Server/Plugins/PluginHost.cs` (`LoadedPlugin` + `TryConfigure` wiring), `EverythingBox.Server/Program.cs` (read back + DI)
- Modify: `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` + `MetadataContractTests.cs` (version pins)
- Create: `EverythingBox.Server.Tests/MusicRegistryTests.cs`

**Interfaces:**
- Produces: `IMusicLibrary` + DTOs; `IPluginRegistry.AddMusicLibrary(IMusicLibrary)`; `PluginRegistry.MusicLibrary`; `LoadedPlugin.MusicLibrary`.

- [ ] **Step 1: `IMusicLibrary.cs`** — the interface + DTOs (Subsonic-shaped; the endpoints in Inc 3/4 map these straight to XML/JSON):
```csharp
namespace EverythingBox.Server.Abstractions;

public sealed record MusicFolderInfo(string Id, string Name);
public sealed record ArtistInfo(string Id, string Name, int AlbumCount, string? CoverArtId);
public sealed record AlbumInfo(string Id, string Name, string ArtistId, string Artist,
    int? Year, string? Genre, int SongCount, int DurationSec, string? CoverArtId, bool Starred);
public sealed record SongInfo(string Id, string Title, string AlbumId, string Album, string ArtistId, string Artist,
    int? Track, int? Disc, int? Year, string? Genre, int? DurationSec, string Suffix, string ContentType,
    long? SizeBytes, string? CoverArtId, bool Starred);
public sealed record PlaylistInfo(string Id, string Name, int SongCount, int DurationSec, IReadOnlyList<SongInfo> Songs);
public sealed record SearchResult(IReadOnlyList<ArtistInfo> Artists, IReadOnlyList<AlbumInfo> Albums, IReadOnlyList<SongInfo> Songs);

/// <summary>The music-domain surface a Subsonic/OpenSubsonic API serves from. Implemented by a
/// music-library plugin and registered via <see cref="IPluginRegistry.AddMusicLibrary"/>. All reads
/// are best-effort snapshots of the scanned library; the mutating calls persist small local state
/// (stars, listening history, playlists) — this server has no user model, so it is a single identity.</summary>
public interface IMusicLibrary
{
    IReadOnlyList<MusicFolderInfo> Folders();
    IReadOnlyList<ArtistInfo> Artists();
    /// <summary>An artist with its albums, or null if unknown.</summary>
    (ArtistInfo Artist, IReadOnlyList<AlbumInfo> Albums)? Artist(string id);
    /// <summary>An album with its songs, or null.</summary>
    (AlbumInfo Album, IReadOnlyList<SongInfo> Songs)? Album(string id);
    SongInfo? Song(string id);
    /// <summary>type ∈ newest/alphabeticalByName/random/byYear/byGenre/recent/frequent/starred.
    /// Unknown types return empty (the route reports the honest error).</summary>
    IReadOnlyList<AlbumInfo> AlbumList(string type, int size, int offset, string? genre, int? fromYear, int? toYear);
    SearchResult Search(string query, int artistCount, int albumCount, int songCount);
    IReadOnlyList<SongInfo> RandomSongs(int size, string? genre);

    /// <summary>The cover file for a cover-art id (album/artist/song), or null. path is a real file.</summary>
    (string Path, string ContentType)? CoverArt(string coverArtId);
    /// <summary>Serve a song's bytes with Range; null when the id is not a contained track.</summary>
    Task<ProxyResponse?> OpenTrackAsync(string songId, string? rangeHeader, CancellationToken ct);

    void Scrobble(string songId, DateTimeOffset playedAt);
    void SetStarred(string id, bool starred);
    IReadOnlyList<PlaylistInfo> Playlists();
    PlaylistInfo? Playlist(string id);
}
```

- [ ] **Step 2: `IPluginRegistry.AddMusicLibrary`** — add to `IPlugin.cs` after `AddProviderTracker`:
```csharp
    /// <summary>Register the music library a Subsonic-style API serves from. At most one applies
    /// across the whole server; a second registration is a configuration mistake and throws.</summary>
    void AddMusicLibrary(IMusicLibrary music);
```

- [ ] **Step 3: `PluginRegistry`** — mirror the `ProviderTracker` at-most-one field exactly (a private `IMusicLibrary? _musicLibrary`, a `public IMusicLibrary? MusicLibrary => _musicLibrary` prop, and `AddMusicLibrary` that `ArgumentNullException.ThrowIfNull` + throws `InvalidOperationException` if already set — do NOT probe the instance, matching the existing "deliberately does NOT …" comments).

- [ ] **Step 4: `PluginHost`** — add `IMusicLibrary? MusicLibrary = null` to the `LoadedPlugin` record (last param, defaulted), and pass `registry.MusicLibrary` in the `new LoadedPlugin(...)` at the end of `TryConfigure` (extend the existing log line's counts if you like, optional).

- [ ] **Step 5: `Program.cs` readback + DI** — after the plugins are loaded (near the `pluginMetadata`/`pluginIndexers` reads ~lines 100/146), add:
```csharp
    var musicLibrary = plugins.Select(p => p.MusicLibrary).FirstOrDefault(m => m is not null);
    if (musicLibrary is not null)
        builder.Services.AddSingleton(musicLibrary);
```
(First registered wins; there is at most one per plugin and the coming `MapSubsonic` resolves `IMusicLibrary` from DI. If the DI registration must happen before `builder.Build()`, place it in the same phase the other plugin readbacks feed services — match the existing ordering; if plugins are loaded after `Build()`, register the `IMusicLibrary` wherever `SourceRouter` is composed and hand it to `MapSubsonic` directly in Increment 3 instead of DI. Follow whatever the existing plugin-source wiring does — read `Program.cs:64-152` and mirror it.)

- [ ] **Step 6: API bump + version tests** — `ServerApi.VersionString` "1.16" → "1.17"; update the two version-pin tests to Minor 17 + add `[InlineData(1, 16)]` to the compat theory.

- [ ] **Step 7: `MusicRegistryTests`** — a fake `IMusicLibrary` + a `PluginRegistry`: `AddMusicLibrary` sets `MusicLibrary`; a second `AddMusicLibrary` throws `InvalidOperationException`; `AddMusicLibrary(null!)` throws `ArgumentNullException`; a fresh registry's `MusicLibrary` is null.

- [ ] **Step 8: Build + test + commit**
Run: `dotnet build EverythingBoxServer.sln -c Debug -clp:ErrorsOnly` then `dotnet test EverythingBox.Server.Tests -v minimal` + `dotnet test EverythingBox.Server.Core.Tests -v minimal` — all green (existing plugins unaffected; version pins at 1.17; Core-BCL-only green).
```bash
git add EverythingBox.Server.Abstractions/Music/IMusicLibrary.cs EverythingBox.Server.Abstractions/IPlugin.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server/Plugins/PluginRegistry.cs EverythingBox.Server/Plugins/PluginHost.cs EverythingBox.Server/Program.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Tests/MusicRegistryTests.cs
git commit -m "feat: IMusicLibrary plugin capability + registration chain (API 1.17)"
```

---

### Task 2: `MusicLibrarySource` implements `IMusicLibrary` (+ local star/scrobble/playlist store)

**Files:**
- Modify: `EverythingBox.Server.MusicLibrary/MusicLibrarySource.cs` (implement `IMusicLibrary`)
- Create: `EverythingBox.Server.MusicLibrary/MusicStateStore.cs` (stars/scrobbles/playlists JSON)
- Modify: `EverythingBox.Server.MusicLibrary/MusicLibraryPlugin.cs` (call `AddMusicLibrary`)
- Create: `EverythingBox.Server.Tests/MusicLibraryImplTests.cs`

**Interfaces:**
- Consumes: `IMusicLibrary` + DTOs (Task 1), the internal `MusicIndex` (Inc 1).

- [ ] **Step 1: `MusicStateStore.cs`** — a small best-effort JSON store in the plugin cache dir (mirror `FileResolverCache`'s temp-then-move durability), holding: a set of starred ids, a scrobble history list (`{songId, playedAt}`), and playlists (`{id, name, [songId]}`). Methods: `bool IsStarred(id)`, `SetStarred(id,bool)`, `Scrobble(songId, time)`, `IReadOnlyList<(string Id,string Name,IReadOnlyList<string> SongIds)> Playlists()`. All swallow IO errors (never throw out of a request). Loaded once, persisted on mutate.

- [ ] **Step 2: implement `IMusicLibrary` on `MusicLibrarySource`** — map the internal `MusicIndex` (built by the scanner) to the Abstractions DTOs:
  - `Folders()` → one `MusicFolderInfo("1","Music")` (single folder v1).
  - `Artists()` → each `MusicArtist` → `ArtistInfo(Id, Name, Albums.Count, coverArtId=first album cover id)`.
  - `Artist(id)` → the artist + its albums as `AlbumInfo`.
  - `Album(id)` → the album + its tracks as `SongInfo` (Suffix = extension w/o dot, ContentType via `MimeFor`, SizeBytes via `new FileInfo(path).Length` best-effort, CoverArtId = the album cover id, Starred via the store).
  - `Song(id)` → the track as `SongInfo`.
  - `AlbumList(type,size,offset,genre,fromYear,toYear)` → all albums sorted by the type (newest=by year desc, alphabeticalByName, random=shuffle by a fixed-seed-per-call is fine, byYear range, byGenre filter, starred=only starred), paged by size/offset; unknown type → empty.
  - `Search(query,…)` → substring match (case-insensitive) over artist/album/song names, capped by the counts.
  - `RandomSongs(size,genre)` → up to `size` random tracks (optionally genre-filtered).
  - `CoverArt(coverArtId)` → decode the id to a path (cover ids are `EncodeId(coverPath)`); if it resolves to a contained file, return `(path, imageMime)`.
  - `OpenTrackAsync(songId, range, ct)` → `_files.OpenAsync(songId, range, ct)` (the track id is `EncodeId(path)`), gated on the id being a known track (`_index.Track(songId) is not null`) so a non-track id serves nothing.
  - `Scrobble`/`SetStarred`/`Playlists`/`Playlist` → the `MusicStateStore`.
  - Cover-art ids: expose a stable cover-art id per album/artist/song = the `EncodeId(coverPath)` of its cover (null when no cover). Keep `IMediaSource` behavior (Inc 1) unchanged.

- [ ] **Step 3: `MusicLibraryPlugin` registers the capability** — in `Configure`, after constructing the source: `var src = new MusicLibrarySource(...); registry.AddSource(src); registry.AddMusicLibrary(src);` (one instance, both interfaces). Pass the state-store path (plugin cache dir) into the source ctor.

- [ ] **Step 4: `MusicLibraryImplTests`** — over a temp tagged tree (reuse the fixture helper): `Artists()`/`Artist(id)`/`Album(id)`/`Song(id)` return the scanned library with the right fields (Suffix/ContentType/coverArtId); `AlbumList("alphabeticalByName",…)` orders; `Search("…")` finds by name; `CoverArt(coverId)` returns the sibling cover path + image mime; `OpenTrackAsync(songId,"bytes=0-9")` → 206; a non-track id → null; `SetStarred(songId,true)` then `Song(songId).Starred` is true and survives a reload (persisted); `Scrobble` records; a `Playlist` round-trips through the store.

- [ ] **Step 5: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green.
```bash
git add EverythingBox.Server.MusicLibrary/MusicLibrarySource.cs EverythingBox.Server.MusicLibrary/MusicStateStore.cs EverythingBox.Server.MusicLibrary/MusicLibraryPlugin.cs EverythingBox.Server.Tests/MusicLibraryImplTests.cs
git commit -m "feat: MusicLibrarySource implements IMusicLibrary — DTO mapping + local stars/scrobbles/playlists"
```

---

## Self-review

**Spec coverage (Increment 2):** `IMusicLibrary` capability + DTOs (spec §Inc2) → Task 1 Step 1. Registration chain extended (spec) → Task 1 Steps 2-5. API 1.16→1.17 + version tests (spec) → Task 1 Step 6. At-most-one, no instance probing (spec) → Task 1 Step 3. `MusicLibrarySource` implements it + local state store (spec) → Task 2. ✅

**Placeholder scan:** the interface + DTOs are concrete; the registration mirrors the enumerated `ProviderTracker` pattern; the DI readback flags the one ordering unknown (register in DI vs hand to `MapSubsonic`) with instruction to mirror the existing plugin-source wiring; the impl maps named `MusicIndex` fields to named DTO fields.

**Type consistency:** `IMusicLibrary` methods + `MusicFolderInfo`/`ArtistInfo`/`AlbumInfo`/`SongInfo`/`PlaylistInfo`/`SearchResult` defined Task 1, implemented Task 2, consumed by the Subsonic routes in Inc 3/4. `IPluginRegistry.AddMusicLibrary` → `PluginRegistry.MusicLibrary` → `LoadedPlugin.MusicLibrary` → `Program.cs` DI, one chain. `ServerApi.VersionString="1.17"` + Minor 17 pins + `[InlineData(1,16)]`. `MusicLibrarySource` gains the interface without changing its `IMediaSource` behavior. ✅
