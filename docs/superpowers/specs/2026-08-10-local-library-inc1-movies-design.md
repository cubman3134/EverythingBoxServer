# Local Library plugin — Increment 1: movies with Range serving

**Status:** approved 2026-08-10, ready for planning.

## Where this fits

EBS#8 is a first-party, in-repo, production-quality local-library plugin, built in four
increments on one new project `EverythingBox.Server.LocalLibrary` (key `"locallib"`):

1. **This increment** — scan movie roots, classify movies, serve with real HTTP Range + path
   security. Delivers "my movie files play, with seeking," and the reusable byte-range slicer.
2. Series roots + episode classification + series→episodes expansion.
3. Kodi `.nfo` sidecars + local artwork.
4. Incremental mtime rescan + music.

Increments 2–4 are **out of scope here** (own specs later).

## Goal

A user configures movie folders; the plugin lists their movies in a `movies` catalog and serves
each file with correct HTTP Range (206/`Content-Range`) so every client — including cast targets
— can seek. No external source is named; the plugin only appears once staged into `plugins/locallib/`
and configured, so a fresh checkout still serves nothing.

## Project shape (mirror `EverythingBox.Server.SampleSource`)

New project `EverythingBox.Server.LocalLibrary`:
- `net9.0`, `IsPackable=false`, nullable enabled.
- A single `ProjectReference` to `EverythingBox.Server.Abstractions` with **`Private="false"`**
  (the host supplies Abstractions; a second copy makes every cast fail).
- Added to the `.sln`; referenced by the **Tests project only** (`EverythingBox.Server.Tests`), never
  by `EverythingBox.Server` — so the main server has no compile-time link and a fresh checkout has
  no `plugins/` dir to load it from.
- A short `README.md` documenting the config section and the "copy only the plugin DLL into
  `plugins/locallib/`, never Abstractions.dll" install step (mirror SampleSource's README).

## Config

```csharp
public sealed class LocalLibraryConfig
{
    /// <summary>Absolute paths to folders whose video files are treated as movies.</summary>
    public List<string> Movies { get; set; } = [];
}
```
Read in `Configure` via `context.GetConfig<LocalLibraryConfig>() ?? new LocalLibraryConfig()`.
Config JSON: `"Plugins": { "locallib": { "Movies": ["D:/Movies", ...] } }`. (Later increments add
`Series`, `Music`.)

## Components

### `LocalLibraryPlugin : IPlugin`
- `Key => "locallib"`, `DisplayName => "Local Library"`, `ApiVersion => new(ServerApi.VersionString)`.
- `Configure`: read config, `registry.AddSource(new MovieLibrarySource(config.Movies, context.Loggers.CreateLogger<MovieLibrarySource>()))`.

### `MovieLibrarySource : IMediaSource`
- `Key => "locallib"`.
- **`Catalogs`** — declare the movies catalog **only when at least one root is configured**
  (mirroring `MetadataBackedVideoSource`: "a shelf is only worth declaring if something can fill
  it"): `Movies.Count == 0 ? [] : [new CatalogDescriptor("movies", "Movies", "movie")]`.
- **`SearchAsync(catalogId, query, ctx, ct)`** (browse when `query` is null, else filter):
  - For each configured root that exists, `Directory.EnumerateFiles(root, "*", options)` with
    `EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
    IgnoreInaccessible = true }` (the SampleSource walk options — avoids following junctions and
    dying on a permission error).
  - Keep files whose extension is in `MediaFileMatcher.VideoExtensions`.
  - For each, derive the display title from `new DefaultReleaseParser().Parse(Path.GetFileNameWithoutExtension(path), MediaType.Movie)`:
    `Title = NormalizedTitle` (fall back to the filename stem if empty), append `(Year)` when
    `Year` is set. `Subtitle` = the file's parent-folder name (context without leaking the full path).
  - When `query` is non-null, keep only items whose `Title` contains it (case-insensitive).
  - Mint `CatalogItem(Id: EncodeId(fullPath), Title, Subtitle, MediaType: "movie", ThumbnailUrl: null,
    Expandable: false)`. Order by `Title` (ordinal-ignore-case). Return `new SourceCatalog("Movies", items)`.
  - Bound the walk defensively (a cap, e.g. first 5000 matches, to avoid an unbounded response on a
    pathological tree) and set `HasMore` if capped.
- **`DetailAsync`** → `SourceCatalog.Empty("Movies")` (movies don't expand in Inc 1).
- **`ResolveAsync(itemId, index, ctx, ct)`** — resolve+validate the path (below); if invalid return
  null; else return `new SourceStream($"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}", MimeFor(path))`.
  `index` is ignored (one file per item).
- **`OpenAsync(itemId, rangeHeader, ct)`** — the byte-range serving (below).

### Id encoding + path security (mandatory — mirror `LocalFolderSource`)
- `EncodeId(path)` = base64url of the UTF-8 absolute path; `TryDecodeId` reverses it.
- **`ResolveSafePath(itemId) -> string?`**: decode the id to a path; resolve its **real** path
  (`Path.GetFullPath`, and resolve reparse points/symlinks the way `LocalFolderSource.ResolveReal`
  does); confirm the real path is **contained** within one of the configured `Movies` roots (also
  real-resolved), using the same boundary check as `LocalFolderSource.IsContained` (compare against
  `root + separator`, ordinal-ignore-case on Windows). Return null on any failure — a decoded path
  that escapes the roots, a junction pointing outside, a non-existent file. **Every `ResolveAsync`
  and `OpenAsync` re-validates via this — the id is never trusted.**
- Copy `LocalFolderSource`'s containment/real-path helpers into this plugin (Inc 1 keeps them
  local; a future DRY extraction to Abstractions is a separate concern the user declined for now).

## Byte-range serving

The host does NOT slice — on the proxy route it relays whatever `OpenAsync` sets and
`CopyToAsync`es the whole `Body` (`AddonEndpoints.ProxyAsync`), then `await upstream.DisposeAsync()`
(which disposes `Body`, then `Owner`). So `OpenAsync` must do the slicing and set 206/`Content-Range`.

### `RangeRequest` — pure, tested parser
```csharp
internal enum RangeKind { Full, Partial, Unsatisfiable }

internal readonly record struct RangeResult(RangeKind Kind, long Start, long Length);

internal static class RangeRequest
{
    /// <summary>
    /// Parse a single HTTP byte-range header against a known total length.
    /// - null/empty/malformed/multi-range/other-unit → Full (serve the whole file, 200).
    /// - "bytes=start-end", "bytes=start-", "bytes=-suffix" → Partial(start, length), clamped to total.
    /// - a start at/after total (and a non-empty file) → Unsatisfiable (416).
    /// - an empty (0-byte) file with any range → Full.
    /// </summary>
    public static RangeResult Parse(string? header, long totalLength);
}
```
Rules:
- Trim; require prefix `bytes=` (case-insensitive) else `Full`.
- Reject a comma (multi-range) → `Full`.
- Split on `-` into `[a, b]`:
  - both present (`a-b`): `start=a`, `end=min(b, total-1)`, `length=end-start+1`; if `a > end`
    (i.e. `a >= total` or `a > b`) → for `a >= total` `Unsatisfiable`, for `a > b` `Full`.
  - `a-` (open end): `start=a`, `length=total-a`; `a >= total` → `Unsatisfiable`.
  - `-b` (suffix): `start=max(0, total-b)`, `length=total-start`; `b <= 0` → `Full`.
  - any non-numeric part → `Full`.
- `total == 0` → always `Full` (nothing to slice; the 200 path serves an empty body).

### `BoundedReadStream` — read-only, length-limited, owns its inner
```csharp
internal sealed class BoundedReadStream(Stream inner, long length) : Stream
{
    // CanRead=true; CanSeek/CanWrite=false. Reads at most `length` bytes total from `inner`
    // (already Seeked to the slice start), then returns 0. Length => the bounded length.
    // ReadAsync/Read both honor the bound. Dispose/DisposeAsync dispose `inner`.
    // Position tracked for the bound; seeking/writing throw NotSupportedException.
}
```

### `OpenAsync`
```csharp
public async Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
{
    var path = ResolveSafePath(itemId);
    if (path is null) return null;

    var info = new FileInfo(path);
    if (!info.Exists) return null;

    var total = info.Length;
    var mime = MimeFor(path);
    var result = RangeRequest.Parse(rangeHeader, total);

    if (result.Kind == RangeKind.Unsatisfiable)
        return new ProxyResponse(Stream.Null, mime)
        {
            StatusCode = 416, AcceptRanges = "bytes", ContentRange = $"bytes */{total}", ContentLength = 0,
        };

    var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, useAsync: true);
    if (result.Kind == RangeKind.Partial)
    {
        file.Seek(result.Start, SeekOrigin.Begin);
        return new ProxyResponse(new BoundedReadStream(file, result.Length), mime)
        {
            StatusCode = 206, ContentLength = result.Length, AcceptRanges = "bytes",
            ContentRange = $"bytes {result.Start}-{result.Start + result.Length - 1}/{total}",
        };
    }

    return new ProxyResponse(file, mime)
    {
        StatusCode = 200, ContentLength = total, AcceptRanges = "bytes",
    };
}
```
Disposal: for `Partial`, `BoundedReadStream` owns and disposes the `FileStream`; for `Full`, the
`FileStream` is the `Body` and the host's `upstream.DisposeAsync()` disposes it; for `Unsatisfiable`,
`Stream.Null` — no file opened. No `FileStream` leaks per playback.

`MimeFor(path)` — a small extension→MIME map (mkv→video/x-matroska, mp4→video/mp4, avi→video/x-msvideo,
mov→video/quicktime, webm→video/webm, …), default `application/octet-stream`. (If the host already
exposes a shared MIME helper reachable from a plugin, reuse it; otherwise a local map.)

## MediaType vocabulary

Catalog and items use the **protocol string** `"movie"` (the native video type). No `MediaTypeDescriptor`
is needed (movie/series are built in). `MediaType.Movie` is used only to drive `DefaultReleaseParser`.

## Testing (mirror `SampleSourceTests` — construct the source over a temp dir)

- **`RangeRequest.Parse`** (pure, no I/O): `null`/empty → Full; `bytes=0-1023` on total 5000 →
  Partial(0,1024); `bytes=1024-` → Partial(1024,3976); `bytes=-500` → Partial(4500,500); `bytes=4999-`
  → Partial(4999,1); `bytes=5000-` → Unsatisfiable; `bytes=abc`/`bytes=0-1,2-3`/`items=0-1` → Full;
  total 0 with any range → Full.
- **`BoundedReadStream`**: wrapping a `MemoryStream` seeked to offset S with length L yields exactly
  the L bytes `[S, S+L)`, then 0; disposing it disposes the inner stream (assert via a probe stream
  that records disposal).
- **`MovieLibrarySource`** over a temp directory containing a couple of video files (e.g.
  `Some.Movie.2019.1080p.mkv`, `Another Film (2020).mp4`) and a non-video file:
  - `SearchAsync(null)` returns only the videos, titled from the parser (`Some Movie (2019)` etc.),
    MediaType `"movie"`, non-expandable; a non-null query filters by title; the catalog is empty when
    no roots are configured (and `Catalogs` is empty then too).
  - `ResolveAsync` returns a `proxy/locallib/<id>/<name>` URL for a valid item and null for an id
    whose decoded path is outside the roots.
  - `OpenAsync(id, "bytes=0-3")` returns StatusCode 206, `Content-Range: bytes 0-3/<total>`,
    `Content-Length: 4`, and a body of exactly the first 4 bytes; `OpenAsync(id, null)` returns 200
    with the full length; `OpenAsync(id, "bytes=<total>-")` returns 416; `OpenAsync(outside-roots-id, …)`
    returns null.
- No test spawns a process, touches the network, or reads a real browser profile. (Path-containment
  tests use only temp dirs the test created.)

## What binds

- New in-repo plugin only; `EverythingBox.Server` gains **no** reference to it; the fresh-checkout
  "serves nothing" property holds (unconfigured → empty `Catalogs`; unstaged → not loaded).
- Plugin references only Abstractions (`Private="false"`); reuses `DefaultReleaseParser` +
  `MediaFileMatcher.VideoExtensions` for classification; no new `PackageReference`.
- **No host/Abstractions/contract change, no API bump** — the plugin consumes the existing
  `IMediaSource`/`ProxyResponse` contract. `ServerApi.VersionString` unchanged.
- **Cleanliness:** the plugin names no external content source → `RepositoryCleanlinessTests` stays
  green trivially. Keep it that way (no denylisted term in code, paths, or commit messages).
- Path security is mandatory and matches `LocalFolderSource`: decode → real-resolve → containment,
  re-validated on every resolve/open; an id escaping the roots serves nothing.
- Range correctness: 206 + accurate `Content-Range`/`Content-Length` for a partial; 200 + full length
  otherwise; 416 for unsatisfiable; `Accept-Ranges: bytes` always. No `FileStream` leak per request.

## Out of scope (later increments / the issue's exclusions)

- Series/episode classification and expansion (Inc 2); `.nfo` + artwork (Inc 3); incremental mtime
  rescan + music (Inc 4).
- Watch state, transcoding, archive-member browsing (the issue's explicit exclusions).
- Extracting the shared path-security helper to Abstractions (user declined for Inc 1).
- Multi-range requests (rare for media; a multi-range header degrades to a full 200).

## Done when

- With a configured `Movies` root staged as `plugins/locallib/`, the server lists the movies in a
  `movies` catalog (titles parsed from filenames) and serves each with working seek: a ranged request
  returns 206 with the correct slice and `Content-Range`, an unranged request returns 200 with the
  full file, an out-of-range request returns 416; every response advertises `Accept-Ranges: bytes`.
- An item id whose path escapes the configured roots serves nothing (null).
- `RangeRequest.Parse`, `BoundedReadStream`, and `MovieLibrarySource` are unit-tested over temp dirs;
  no host/contract change; both engine test projects + the plugin tests green including
  `RepositoryCleanlinessTests`. Verified in Release.
