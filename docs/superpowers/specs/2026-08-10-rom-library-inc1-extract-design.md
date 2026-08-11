# ROM library (EBS#12) — Increment 1: extract the shared local-file primitive

**Status:** approved 2026-08-10, ready for planning.

## Where this fits

EBS#12 is a ROM-library plugin, a sibling to #8's LocalLibrary, built in increments:

1. **This increment** — extract #8's Range serving + path-containment security + parse cache into a
   reusable **Abstractions** primitive, and refactor `LocalLibrary` to use it (behavior-preserving).
   No ROM code yet — this is the "shared listing abstraction #8 calls for."
2. `EverythingBox.Server.RomLibrary` plugin MVP — per-system `game` catalogs + Range serving.
3. `gamelist.xml` + local art + meta panel.

Hashing (the client can't consume server hashes yet), `.m3u` bundling (client-generated), and
server-side scraping are out of scope for #12 as verified against the client.

## Goal

One audited copy of the security-critical local-file machinery, in Abstractions, used by both the
existing LocalLibrary plugin and the coming RomLibrary plugin. `LocalLibrary`'s behavior is
byte-identical after the refactor (all its tests pass unchanged).

## What moves (from `EverythingBox.Server.LocalLibrary` → `EverythingBox.Server.Abstractions`)

### 1. `RangeRequest` (+ `RangeResult`, `RangeKind`) — public
The pure single-range HTTP byte-range parser, verbatim, `internal` → `public`, namespace
`EverythingBox.Server.Abstractions`. (Its logic — Full/Partial/Unsatisfiable, suffix/open/closed
forms, clamp/`0-length`/multi-range → Full — is unchanged.)

### 2. `BoundedReadStream` — internal to Abstractions
The bounded, read-only, inner-owning stream, verbatim, moved next to `SafeLocalFileServer` and kept
`internal` (only `SafeLocalFileServer.OpenAsync` constructs it).

### 3. `SafeLocalFileServer` — public (NEW class, absorbing LocalLibrary's serving + security)
Constructed over a root set + a MIME map; the single home for id minting, path-containment, and
Range serving. Absorbs `LocalLibrarySource`'s `EncodeId`/`TryDecodeId`, `PathComparison`,
`ResolveReal`, `IsContained`, `ResolveSafePath`, `ResolveSafeDir`, and `OpenAsync` **verbatim in
logic** (only re-homed and parameterized by `roots`/`mimeFor`):
```csharp
namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Serves files that live inside a fixed set of configured roots, safely: an id is a base64url of
/// an absolute path, and every resolve re-decodes it, follows junctions/symlinks to the real path,
/// and confirms containment before anything is opened — an id is never trusted. Range requests are
/// honored (206/Content-Range) via the proxy route. Shared by every local-file source plugin so the
/// containment discipline lives in exactly one audited place.
/// </summary>
public sealed class SafeLocalFileServer
{
    public SafeLocalFileServer(IReadOnlyList<string> roots, Func<string, string> mimeFor);

    /// <summary>base64url of the UTF-8 absolute path. Static — an id is a path, not root-scoped.</summary>
    public static string EncodeId(string absolutePath);
    public static string? TryDecodeId(string id);

    /// <summary>True if <paramref name="path"/> (already confirmed to exist) real-resolves to inside a root.</summary>
    public bool IsContained(string path);

    /// <summary>Decode → GetFullPath → File.Exists → IsContained. Null for any bad/foreign id.</summary>
    public string? ResolveSafeFile(string id);
    /// <summary>Decode → GetFullPath → Directory.Exists → IsContained. Null for any bad/foreign id.</summary>
    public string? ResolveSafeDir(string id);

    /// <summary>Resolve a file id and serve it with Range (206/200/416, Accept-Ranges, correct
    /// Content-Range/Length), content type from the ctor's mimeFor. Null when the id is not a
    /// contained file. No FileStream leak (BoundedReadStream / ProxyResponse disposal).</summary>
    public Task<ProxyResponse?> OpenAsync(string id, string? rangeHeader, CancellationToken ct = default);
}
```
- **The proxy-URL string** (`proxy/{Key}/{id}/{name}`) stays in the PLUGIN (it needs the plugin's
  `Key`); `SafeLocalFileServer` owns only the *serving* half (`OpenAsync`) and *resolution*.
- `mimeFor` is supplied by the caller so each plugin keeps its own extension→MIME map (video/image
  for LocalLibrary; ROM/image for RomLibrary later).
- Containment is **byte-identical** to #8: real-resolve every ancestor reparse point, trailing-
  separator boundary, OS-appropriate `PathComparison`, narrow exception catches → null.

### 4. `LibraryMetaCache` — public, made generic
Move the class to Abstractions and generalize its value type (the mechanism is generic; the record
is per-plugin):
```csharp
public sealed class LibraryMetaCache(IResolverCache? cache)
{
    public Task<T> GetOrComputeAsync<T>(string mediaPath, string? nfoPath, Func<T> compute, CancellationToken ct);
}
```
Same key (`mediaPath|mediaMtime|nfoMtime`), same best-effort semantics (read/write errors →
recompute/swallow; `OperationCanceledException` always propagates), same JSON round-trip — only `T`
becomes a type parameter. `ItemMeta` (LocalLibrary's record) stays in the plugin.

## LocalLibrary refactor (behavior-preserving)

- **Delete** `RangeRequest.cs`, `BoundedReadStream.cs`, and the cache class in `LibraryMetaCache.cs`
  from the plugin (they now live in Abstractions); keep only the `ItemMeta` record in the plugin.
- **`LocalLibrarySource`** constructs two `SafeLocalFileServer`s (ids are static-encoded, so this is
  just about which roots each validation is scoped to):
  - `_files = new SafeLocalFileServer(movieRoots.Concat(seriesRoots).ToList(), MimeFor)` — used for
    file serving (`OpenAsync`), `ResolveSafeFile` (in `ResolveAsync`/`MetaAsync` file branch), and
    the `IsContained` backstop in `ScanMovies`/`ListShows`.
  - `_seriesDirs = new SafeLocalFileServer(seriesRoots, MimeFor)` — used only for `ResolveSafeDir`
    in `DetailAsync`/`MetaAsync` dir branch (preserving #8's "a show folder must be under a SERIES
    root" rule — a movie-root folder must not expand).
  - Ids via `SafeLocalFileServer.EncodeId(path)` (static).
  - `OpenAsync(id, range, ct)` → `_files.OpenAsync(id, range, ct)`.
  - `ResolveAsync` → `_files.ResolveSafeFile(id)` then build the `proxy/{Key}/…` URL (unchanged).
  - `DetailAsync` → `_seriesDirs.ResolveSafeDir(id)`; `MetaAsync` → `_files.ResolveSafeFile(id)`
    (file) else `_seriesDirs.ResolveSafeDir(id)` (dir), null otherwise.
  - `_meta = new LibraryMetaCache(cache)` → `_meta.GetOrComputeAsync<ItemMeta>(...)`.
  - `MimeFor` (the plugin's video+image map) stays in the plugin and is passed to both
    `SafeLocalFileServer` ctors.
  - Remove the now-relocated `EncodeId`/`TryDecodeId`/`ResolveSafePath`/`ResolveSafeDir`/`IsContained`/
    `ResolveReal`/`PathComparison`/`OpenAsync`/`RangeRequest` usage from the plugin.
- **Every existing LocalLibrary test passes unchanged** — the source delegates but behaves identically
  (movie ids byte-identical since `EncodeId` is the same base64url; serving, containment, series
  expansion, caching all preserved). Tests that referenced the plugin's now-`internal`-to-Abstractions
  `EncodeId` switch to `SafeLocalFileServer.EncodeId` (a public static) — a mechanical call-site change,
  not a behavior change.

## Host API bump

`ServerApi.VersionString` 1.13 → 1.14 (new public Abstractions types: `RangeRequest`/`RangeResult`/
`RangeKind`, `SafeLocalFileServer`, generic `LibraryMetaCache`). Additive — existing plugins/sources
are unaffected. Update the two version-pin tests (Minor 14) + add `[InlineData(1, 13)]` to the compat
theory.

## Testing

- **`SafeLocalFileServerTests`** (new, in `EverythingBox.Server.Tests`) — the security surface, over
  temp dirs: an out-of-roots id → `ResolveSafeFile`/`OpenAsync` null; a junction inside a root
  pointing outside → null; a root-prefix false match (`X\Movies` vs `X\Movies-Secret`) → null;
  case handling; `EncodeId`/`TryDecodeId` round-trip; `OpenAsync` 206 (correct `Content-Range`/slice),
  200 (full), 416 (unsatisfiable), with the ctor's `mimeFor` content type; a `ResolveSafeDir` under
  the roots vs a foreign/file id. (These migrate/expand the assertions that lived in
  `LocalLibrarySourceTests` + `RangeRequestTests` + `BoundedReadStreamTests`.)
- **`RangeRequestTests`, `BoundedReadStreamTests`** — retargeted to the Abstractions types (same cases).
- **`LibraryMetaCacheTests`** — retargeted to the generic Abstractions class (`GetOrComputeAsync<ItemMeta>`
  or a small test record); hit/miss/mtime/corrupt/null unchanged.
- **`LocalLibrarySourceTests`** — unchanged assertions; only ctor/`EncodeId` call sites updated. This is
  the behavior-preservation gate: if any Inc 1–4 movie/series/nfo/cache test needs an assertion change,
  the refactor drifted and must be corrected, not the test.
- Version-pin tests → 1.14; compat gains `[InlineData(1, 13)]`.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- **Additive** host change: new public Abstractions types; single API bump 1.13 → 1.14; no existing
  source/plugin behavior changes. `IMediaSource`/`ProxyResponse`/the routes are untouched.
- **Security is byte-identical** — the containment/serving logic moves verbatim; a dedicated security
  review confirms no drift (real-resolve, trailing-separator boundary, `PathComparison`, narrow catches).
- LocalLibrary is refactored to consume the primitive with **zero behavior change** (its full test
  suite is the gate).
- **Cleanliness:** no external content-source name; `RepositoryCleanlinessTests` stays green.
- No new NuGet package.

## Out of scope

- Any ROM/game code (Inc 2+).
- Changing `LocalLibrary`'s catalog/NFO/artwork/classification logic (only the serving/security/cache
  substrate moves; the video-domain logic stays put).
- New serving features (e.g. multi-range) — a straight extraction, not an enhancement.

## Done when

- `RangeRequest`, `SafeLocalFileServer` (+ internal `BoundedReadStream`), and a generic
  `LibraryMetaCache` live in Abstractions with their own tests, including the full containment/Range
  security suite; API is 1.14.
- `LocalLibrary` uses them with byte-identical behavior — its entire Inc 1–4 test suite passes
  unchanged (only ctor/`EncodeId` call sites edited).
- A security review confirms the moved containment is identical; both engine test projects + the
  plugin tests green including `RepositoryCleanlinessTests`. Verified in Release. RomLibrary (Inc 2)
  can now be built on the shared primitive.
