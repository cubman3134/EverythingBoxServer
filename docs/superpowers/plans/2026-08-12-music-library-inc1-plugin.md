# Music library — Increment 1 (the MusicLibrary plugin) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `EverythingBox.Server.MusicLibrary` plugin (`"musiclib"`) that scans configured roots, reads tags via ATL (artist / **album-artist** / album / track / disc / year / genre / duration / embedded cover), builds a `MusicIndex` grouped by album-artist (compilations correct from day one), incrementally by mtime, and serves a native `music` shelf (artists → albums → tracks) with Range. No Subsonic yet (Increments 2–4).

**Architecture:** Clones the LocalLibrary/RomLibrary plugin skeleton. `MusicScanner` reads tags (the plugin's own `z440.atl.core` NuGet dep, isolated by the per-plugin load context) into a `MusicIndex`; `MusicLibrarySource : IMediaSource` exposes the shelf and serves files + covers through a `SafeLocalFileServer`. Incremental rescan reuses `LibraryMetaCache`.

**Tech Stack:** .NET 9 / C#, xUnit, `z440.atl.core` (plugin + plugin-test only). `EverythingBox.Server.MusicLibrary` + `EverythingBox.Server.Tests`.

## Global Constraints

- **The tag reader is a `PackageReference` on the PLUGIN only** (`z440.atl.core`, MIT). Core stays BCL-only; Abstractions is not touched by the dep. Default `Private=true` so `z440.atl.core.dll` + `.deps.json` land in the plugin output and load from `plugins/musiclib/` via `AssemblyDependencyResolver`.
- **Album-artist is the grouping key** — a track's `AlbumArtist` (fall back to `Artist` only when empty); a well-tagged compilation groups under its album-artist ("Various Artists"), never per-track artist.
- **No committed binary media fixtures.** Tests synthesize tagged audio at runtime (ATL's writer over a minimal in-code audio template); `RepositoryCleanlinessTests` (scans contents + full git history) stays green. No external content-source name.
- **In-repo plugin, engine-invisible** — `Private="false"` Abstractions ref, referenced only by the Tests project, loaded from `plugins/musiclib/`; empty catalogs when no roots configured.
- No API bump this increment (plugin-only; the `IMusicLibrary` capability + bump is Increment 2). Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: Scaffold + config + ATL scanner + `MusicIndex` (+ tests)

**Files:**
- Create: `EverythingBox.Server.MusicLibrary/EverythingBox.Server.MusicLibrary.csproj` (clone LocalLibrary's + a `<PackageReference Include="z440.atl.core" Version="…" />`)
- Create: `EverythingBox.Server.MusicLibrary/MusicLibraryConfig.cs`, `MusicIndex.cs` (the records), `MusicScanner.cs`
- Modify: `EverythingBoxServer.sln` (add the project); `EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj` (ProjectReference the plugin + a `PackageReference` on `z440.atl.core` for fixture synthesis)
- Create: `EverythingBox.Server.Tests/MusicScannerTests.cs`

**Interfaces:**
- Produces: `MusicLibraryConfig { List<string> Roots }`; `MusicIndex` with `IReadOnlyList<MusicArtist> Artists` and lookup by id; records `MusicArtist(Id,Name,Albums)`, `MusicAlbum(Id,ArtistId,ArtistName,Name,Year,CoverPath?,Tracks)`, `MusicTrack(Id,Title,TrackNo,DiscNo,DurationSec,Path,ArtistName,AlbumId)`; `MusicScanner.Scan(roots, coverCacheDir, cache?, ct) → MusicIndex`.

- [ ] **Step 1: csproj** — clone LocalLibrary's csproj (TargetFramework net9.0, ImplicitUsings, Nullable, IsPackable=false, `InternalsVisibleTo("EverythingBox.Server.Tests")`, `Private="false"` Abstractions ref) and add `<ItemGroup><PackageReference Include="z440.atl.core" Version="6.*" /></ItemGroup>` (pin the latest 6.x). Add to the solution: `dotnet sln add …`. In `EverythingBox.Server.Tests.csproj`, add a `<ProjectReference>` to the plugin (match the LocalLibrary line form) AND `<PackageReference Include="z440.atl.core" Version="6.*" />` (test synthesizes fixtures).

- [ ] **Step 2: `MusicLibraryConfig.cs`**
```csharp
namespace EverythingBox.Server.MusicLibrary;
public sealed class MusicLibraryConfig
{
    /// <summary>Absolute paths to music library roots (Artist/Album/… trees).</summary>
    public List<string> Roots { get; set; } = [];
}
```

- [ ] **Step 3: `MusicIndex.cs`** — the immutable model + id scheme (ids stable across rescans):
```csharp
using EverythingBox.Server.Abstractions;   // SafeLocalFileServer.EncodeId
using System.Security.Cryptography;
using System.Text;

namespace EverythingBox.Server.MusicLibrary;

public sealed record MusicTrack(string Id, string Title, int? TrackNo, int? DiscNo, int? DurationSec, string Path, string ArtistName, string AlbumId);
public sealed record MusicAlbum(string Id, string ArtistId, string ArtistName, string Name, int? Year, string? CoverPath, IReadOnlyList<MusicTrack> Tracks);
public sealed record MusicArtist(string Id, string Name, IReadOnlyList<MusicAlbum> Albums);

public sealed class MusicIndex
{
    public IReadOnlyList<MusicArtist> Artists { get; }
    private readonly Dictionary<string, MusicArtist> _artistById;
    private readonly Dictionary<string, MusicAlbum> _albumById;
    private readonly Dictionary<string, MusicTrack> _trackById;   // trackId → track

    public MusicIndex(IReadOnlyList<MusicArtist> artists) { /* build the three lookup dicts */ }

    public MusicArtist? Artist(string id) => _artistById.GetValueOrDefault(id);
    public MusicAlbum? Album(string id) => _albumById.GetValueOrDefault(id);
    public MusicTrack? Track(string id) => _trackById.GetValueOrDefault(id);
    public static readonly MusicIndex Empty = new([]);

    // Stable, deterministic ids. Track/cover ids are base64url of the absolute path (so they round-trip
    // through the SafeLocalFileServer for serving). Artist/album ids are content hashes of the grouping
    // key so they survive a rescan (a path can't identify an album that spans files).
    public static string TrackId(string path) => SafeLocalFileServer.EncodeId(path);
    public static string ArtistId(string albumArtist) => "ar-" + Hash(albumArtist);
    public static string AlbumId(string albumArtist, string album) => "al-" + Hash(albumArtist + "\n" + album);
    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).ToLowerInvariant()[..16];
}
```

- [ ] **Step 4: `MusicScanner.cs`** — walk roots, read tags (ATL), aggregate by album-artist → album → track, incremental by mtime via `LibraryMetaCache`.
  - Extensions: `{.mp3,.flac,.m4a,.ogg,.opus,.wav}` (case-insensitive).
  - Per file: get a cached `TrackTags` record (`internal sealed record TrackTags(string? Artist, string? AlbumArtist, string? Album, string? Title, int? TrackNo, int? DiscNo, int? Year, string? Genre, int? DurationSec, bool HasEmbeddedCover)`) via `_cache.GetOrComputeAsync<TrackTags>(path, null, () => ReadTags(path), ct)` (keyed by `path|mtime`). `ReadTags` uses `var t = new ATL.Track(path);` and maps `t.Artist`, `t.AlbumArtist`, `t.Album`, `t.Title`, `t.TrackNumber`, `t.DiscNumber`, `t.Year`, `t.Genre`, `t.Duration` (seconds), `t.EmbeddedPictures.Count > 0`. Any read failure → a `TrackTags` with all-null (the file still lists under "Unknown Artist"/its filename — never throw out of a scan).
  - **Grouping:** `albumArtist = AlbumArtist ?? Artist ?? "Unknown Artist"`; `album = Album ?? "Unknown Album"`; `title = Title ?? Path.GetFileNameWithoutExtension(path)`. Build `MusicArtist`(by album-artist) → `MusicAlbum`(by album-artist+album) → `MusicTrack`s, tracks sorted by `(DiscNo ?? 1, TrackNo ?? 0, Title)`, albums sorted by `(Year, Name)`, artists by Name.
  - **Cover:** for each album, `CoverPath` = the FIRST of: a sibling `cover.*`/`folder.*` (jpg/jpeg/png/webp) in the album's directory; else, if a track has an embedded picture, extract it ONCE to `{coverCacheDir}/{albumId}.{ext}` (via `t.EmbeddedPictures[0]` bytes; write temp-then-move) and use that path; else null.
  - Signature: `public async Task<MusicIndex> ScanAsync(IReadOnlyList<string> roots, string coverCacheDir, LibraryMetaCache cache, CancellationToken ct)`. Skip a missing/blank root. Cap total tracks (e.g. 100000) defensively.
  - **The ATL API surface is the one novelty** — confirm the exact `ATL.Track` property names (`Artist`/`AlbumArtist`/`Album`/`Title`/`TrackNumber`/`DiscNumber`/`Year`/`Genre`/`Duration`/`EmbeddedPictures`) against the referenced z440.atl.core version and adjust if a name differs; note any deviation.

- [ ] **Step 5: `MusicScannerTests.cs`** — synthesize tagged audio at runtime, NO committed binary.
  - A fixture helper `WriteTaggedTrack(dir, fileName, artist, albumArtist, album, title, trackNo, discNo, year)` that creates a minimal valid audio file then writes tags with ATL. **Determine the reliable synthesis mechanism with the referenced ATL version**: preferred — write a minimal valid MP3 template (a small in-code `byte[]` of a silent MPEG frame + empty ID3, a few hundred bytes — this is source, not a committed fixture) to the path, then `var t = new ATL.Track(path); t.Artist = …; t.AlbumArtist = …; …; t.Save();`. If ATL can create a container on Save for a chosen format, use that instead. Whichever works deterministically on this ATL version — the constraint is only "tagged audio synthesized at runtime, nothing binary committed."
  - Assert: a **compilation** (three tracks, same `album`, different `artist`, same `albumArtist="Various Artists"`) groups under ONE artist "Various Artists" with ONE album holding all three tracks (the classic bug guarded); tracks sort by (disc, track); a track with no album-artist falls back to its artist; `MusicIndex.Track(TrackId(path))`/`Album(...)`/`Artist(...)` round-trip; a sibling `cover.jpg` is picked as the album `CoverPath`; a second scan with an unchanged file does not re-read tags (assert via a counting cache/`LibraryMetaCache` hit — or that `ScanAsync` with a populated cache returns the same index without re-parsing); a corrupt/non-audio file with a music extension does not throw and lists under Unknown.

- [ ] **Step 6: Build + test + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` (green incl. `MusicScannerTests`, `RepositoryCleanlinessTests`, and the two Core-BCL-only tests still green — the dep is on the plugin, not Core).
```bash
git add EverythingBox.Server.MusicLibrary/EverythingBox.Server.MusicLibrary.csproj EverythingBox.Server.MusicLibrary/MusicLibraryConfig.cs EverythingBox.Server.MusicLibrary/MusicIndex.cs EverythingBox.Server.MusicLibrary/MusicScanner.cs EverythingBoxServer.sln EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj EverythingBox.Server.Tests/MusicScannerTests.cs
git commit -m "feat: MusicLibrary scanner + index — ATL tags, album-artist grouping, incremental by mtime"
```

---

### Task 2: `MusicLibrarySource : IMediaSource` — the native music shelf

**Files:**
- Create: `EverythingBox.Server.MusicLibrary/MusicLibrarySource.cs`, `MusicLibraryPlugin.cs`
- Create: `EverythingBox.Server.Tests/MusicLibrarySourceTests.cs`

**Interfaces:**
- Consumes: `MusicIndex`/`MusicScanner` (Task 1), `SafeLocalFileServer`, `LibraryMetaCache`, `IMediaSource`/`CatalogDescriptor`/`CatalogItem`/`SourceCatalog`/`SourceStream`.
- Produces: `MusicLibrarySource(IReadOnlyList<string> roots, string coverCacheDir, IResolverCache? cache, ILogger)`; `MusicLibraryPlugin : IPlugin` (key `"musiclib"`).

- [ ] **Step 1: `MusicLibrarySource.cs`** — mirror `RomLibrarySource`'s serving pattern.
  - ctor builds `_files = new SafeLocalFileServer([.. roots, coverCacheDir], MimeFor)` (covers in the cache dir are servable too) and a `LibraryMetaCache` for the scanner; holds a lazily-(re)built `MusicIndex` (rebuild when a scan is requested; MVP: build on first access + on each `SearchAsync("music")` call is acceptable, or cache the index and rescan on demand — build once per source construction and rescan lazily; keep it simple: hold `_index`, (re)build via the scanner, guarded so a concurrent browse doesn't double-scan).
  - `Key => "musiclib"`; `Catalogs => roots.Count > 0 ? [new CatalogDescriptor("music", "Music", "music")] : []`.
  - `SearchAsync("music", query, …)` → the **artists** as `CatalogItem`s: `Id=artist.Id`, `Title=artist.Name`, `MediaType="music"`, `Expandable=true`, `ThumbnailUrl` = the first album cover if any (via the proxy URL). Filter by `query` on name. (Artists-first is the Subsonic-shaped default; a flat album view can come later.)
  - `DetailAsync(id, …)` → if `id` is an artist id (`_index.Artist(id)`), return its **albums** as expandable `CatalogItem`s (`Id=album.Id`, `Title=album.Name` (+ year), `Subtitle=artist.Name`, `ThumbnailUrl`=cover, `Expandable=true`); if `id` is an album id (`_index.Album(id)`), return its **tracks** as non-expandable items (`Id=track.Id`, `Title` = `TrackNo. Title`, `Subtitle`=album/artist, `MediaType="music"`); else empty.
  - `ResolveAsync(id, …)` → `_index.Track(id)` is a track → `proxy/musiclib/{id}/{Uri.EscapeDataString(fileName)}` + `MimeFor(path)`; else null.
  - `OpenAsync(id, range, ct)` → `_files.OpenAsync(id, range, ct)` (serves both track files and cover images — both contained). Cover URL for a `CoverPath` = `proxy/musiclib/{EncodeId(coverPath)}/{escaped name}`.
  - `MimeFor`: `.mp3`→audio/mpeg, `.flac`→audio/flac, `.m4a`→audio/mp4, `.ogg`→audio/ogg, `.opus`→audio/opus, `.wav`→audio/wav, `.jpg/.jpeg`→image/jpeg, `.png`→image/png, `.webp`→image/webp, else application/octet-stream.

- [ ] **Step 2: `MusicLibraryPlugin.cs`** — mirror `RomLibraryPlugin` (key `"musiclib"`, `GetConfig<MusicLibraryConfig>()`, a `FileResolverCache(Path.Combine(context.CacheDirectory,"meta"),16MB)`, and pass `Path.Combine(context.CacheDirectory,"covers")` as the coverCacheDir; `registry.AddSource(new MusicLibrarySource(config.Roots, coverDir, cache, logger))`).

- [ ] **Step 3: `MusicLibrarySourceTests.cs`** — over a temp tree of synthesized tagged tracks (reuse Task 1's fixture helper):
  - no roots → empty `Catalogs`; a configured root → a `music` catalog.
  - `SearchAsync("music")` → the artists (compilation under "Various Artists"); `DetailAsync(artistId)` → its albums; `DetailAsync(albumId)` → its tracks sorted by (disc, track), titles `NN. Title`.
  - `ResolveAsync(trackId)` → a `proxy/musiclib/…` URL + `audio/*` mime; a foreign id → null.
  - `OpenAsync(trackId, "bytes=0-9", …)` → 206 with the sliced bytes; a cover id → 200 image.
  - traversal: an id outside the roots serves/lists nothing.

- [ ] **Step 4: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green.
```bash
git add EverythingBox.Server.MusicLibrary/MusicLibrarySource.cs EverythingBox.Server.MusicLibrary/MusicLibraryPlugin.cs EverythingBox.Server.Tests/MusicLibrarySourceTests.cs
git commit -m "feat: MusicLibrarySource — a native music shelf (artists/albums/tracks) with Range serving"
```

---

## Self-review

**Spec coverage (Increment 1):** ATL tag scan, album-artist grouping, incremental by mtime, covers (spec §Inc1) → Task 1. Native `music` shelf artists→albums→tracks + Range serving (spec) → Task 2. Plugin NuGet dep isolated to the plugin (spec) → Task 1 Step 1 csproj. No committed binary fixtures (spec) → Task 1 Step 5 runtime synthesis. No API bump (spec: capability is Inc 2) → nothing touches Abstractions/ServerApi. ✅

**Placeholder scan:** the id scheme, index model, scanner aggregation, and source routing are concrete; the two genuine unknowns (exact ATL property names; the runtime fixture-synthesis mechanism) are explicitly flagged for the implementer to pin against the referenced ATL version, with the invariant stated (no committed binary). Not hand-waving — bounded discovery with a clear constraint.

**Type consistency:** `MusicScanner.ScanAsync(roots, coverCacheDir, LibraryMetaCache, ct) → MusicIndex`; `MusicIndex.Artist/Album/Track(id)` + `TrackId/ArtistId/AlbumId`; `MusicLibrarySource(IReadOnlyList<string>, string, IResolverCache?, ILogger)` consuming them; `MusicLibraryPlugin` mirrors `RomLibraryPlugin`. `CatalogDescriptor("music","Music","music")` / `CatalogItem(... MediaType:"music", Expandable)` match the (post-#4) `Kind` member — use the current `CatalogItem` shape (the `Kind` member, since #4 renamed it). ✅
