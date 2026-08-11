# Local Library plugin — Increment 3: `.nfo` metadata + local artwork

**Status:** approved 2026-08-10, ready for planning.

## Where this fits

EBS#8, increment 3 of 4. Increments 1 (movies + Range) and 2 (series) are merged. This increment
reads Kodi `.nfo` sidecars and local artwork so the library shows real titles/years/plots and
posters instead of parsed filenames. Unlike Inc 1/2 it takes a small, **additive host contract
change** — the client already calls `/meta/{type}/{id}.json` and renders a detail panel from it,
but the route returns `{}` today, so there is nowhere for a plot/poster to go without it.

## What the client actually consumes (verified against the EverythingBox app)

- The detail page calls `GET /meta/{type}/{id}.json` and renders from a **flat** `MediaDetail`
  (NOT nested under a `meta` key — that is the Stremio-only path this server does not use):
  ```json
  { "title": "...", "subtitle": "...", "overview": "plain-text plot",
    "image": "poster URL (relative proxy/... is base-resolved)",
    "facts": [ { "label": "Year", "value": "1999" } ] }
  ```
  Plot → **`overview`** (plain text, HTML shown literally). Poster → **`image`** (a relative
  `proxy/...` path is resolved against the addon base, so a local poster works). Year/runtime →
  **`facts`** rows (`{label,value}`; empty values dropped). A detail is "valid" if title OR
  overview OR facts OR art is non-empty; an empty object leaves the placeholder poster.
- Catalog/detail rows read `{id,title,subtitle,type,thumbnailUrl,url,mime,expandable}` + art
  roles; a relative `thumbnailUrl` is base-resolved. Descriptive fields (overview/plot/etc.) on a
  row are ignored — so richness for the panel goes on `/meta`, and the poster on both the row
  (`thumbnailUrl`) and the panel (`image`).
- **Background:** only themed detail views render it, and art-role URLs are NOT base-resolved
  (they must be absolute). A local file can't emit an absolute URL for itself, so **background is
  out of scope** here — poster via `image` is the win.

## Part A — Host: a rich-detail meta surface (additive, API 1.12 → 1.13)

### A1. DTOs (`EverythingBox.Server.Abstractions`)
New file `EverythingBox.Server.Abstractions/Metadata/SourceDetail.cs`:
```csharp
namespace EverythingBox.Server.Abstractions;

/// <summary>One labelled fact row on an item's detail panel (e.g. Year, Runtime).</summary>
public sealed record MetaFact(string Label, string Value);

/// <summary>
/// Rich per-item detail for the meta panel. A source returns this from
/// <see cref="IMediaSource.MetaAsync"/>; the host serializes it to the flat shape the client's
/// detail page reads. <see cref="ImageUrl"/> may be a relative <c>proxy/{key}/{id}/{name}</c>
/// path (the client resolves it against the addon base). Text is plain (not HTML).
/// </summary>
public sealed record SourceDetail(
    string Title,
    string? Subtitle = null,
    string? Overview = null,
    string? ImageUrl = null,
    IReadOnlyList<MetaFact>? Facts = null);
```

### A2. `IMediaSource.MetaAsync` — optional, default null
Add to `IMediaSource` (mirroring the `OpenAsync`/`WarmUpAsync` default-method idiom, so every
existing source is unaffected):
```csharp
    /// <summary>Optional. Rich detail for the meta panel of one item. Default: no rich metadata
    /// (the meta route returns an empty object, the client shows a blank-but-valid panel).</summary>
    Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceDetail?>(null);
```

### A3. The `meta` route calls it (`EverythingBox.Server/AddonEndpoints.cs`)
Replace the `{}` handler with one that resolves the source and calls `MetaAsync`, wrapped in the
same cancellation-vs-throw discipline as `DetailAsync`:
```csharp
        app.MapGet($"{prefix}/meta/{{type}}/{{id}}.json", MetaAsync);
```
```csharp
    internal static async Task<IResult> MetaAsync(
        string type, string id, SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!router.TryResolve(id, out var source, out var payload))
            return Results.Json(new { });                    // unknown id → blank panel (as today)
        try
        {
            var detail = await source.MetaAsync(payload, new SourceContext(), ct);
            return detail is null ? Results.Json(new { }) : Results.Json(ToWireMeta(detail));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            loggers.CreateLogger("Meta").LogError(ex,
                "meta {Type}/{Id}: source '{Source}' threw — returning empty", type, id, PluginDiagnostics.SafeLabel(source));
            return Results.Json(new { });
        }
    }

    private static object ToWireMeta(SourceDetail d) => new
    {
        title = d.Title,
        subtitle = d.Subtitle,
        overview = d.Overview,
        image = d.ImageUrl,                                  // relative proxy path OK; client resolves it
        facts = (d.Facts ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new { label = f.Label, value = f.Value })
            .ToArray(),
    };
```
Backward compatible: a source that doesn't override `MetaAsync` → null → `{}` (today's behavior);
the two built-in sources and SampleSource are unaffected.

### A4. Version + tests
`ServerApi.VersionString` 1.12 → 1.13; update the two version-pin tests (Minor 13) and add
`[InlineData(1, 12)]` to the compat theory. Add an `AddonEndpoints`/route test that a stub source
implementing `MetaAsync` makes `/meta` emit `{title, overview, image, facts:[…]}`, and that a
source without it still yields `{}`.

## Part B — Plugin: read `.nfo` + artwork (`EverythingBox.Server.LocalLibrary`)

### B1. `NfoReader` — hardened, tolerant XML
New file `NfoReader.cs`. A `record NfoInfo(string? Title, int? Year, string? Plot)` and:
```csharp
internal static class NfoReader
{
    // Reads <title>/<year>/<plot> from a Kodi .nfo (movie, tvshow, or episodedetails root — all
    // carry these child elements). Namespace-agnostic (LocalName). Tolerant: any failure → null.
    public static NfoInfo? TryRead(string nfoPath);
}
```
Parse with a hardened reader — **this establishes the repo's safe-XML pattern for untrusted files**:
```csharp
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var stream = File.OpenRead(nfoPath);
        using var reader = XmlReader.Create(stream, settings);
        var doc = XDocument.Load(reader);
```
Read `Descendants().FirstOrDefault(e => e.Name.LocalName == "title")` etc. (first `title`, `year`,
`plot`), tolerant of any exception (`XmlException`, `IOException`, …) → return null. A blank/absent
field stays null. `year` parses an int (else null).

### B2. `ArtworkFinder` — locate a poster
New file `ArtworkFinder.cs`:
```csharp
internal static class ArtworkFinder
{
    // For a media FILE: "<stem>-poster.<img>", then "poster.*"/"folder.*" in the same directory.
    // For a show FOLDER: "poster.*"/"folder.*" in that folder.
    // <img> ∈ { jpg, jpeg, png, webp }. Returns the first existing path, or null.
    public static string? PosterFor(string mediaPathOrDir);
}
```
Probe a small ordered candidate list; return the first `File.Exists`. Case-insensitive extension.

### B3. `NfoFor` helpers
- Movie/episode FILE `f`: the sidecar `Path.ChangeExtension(f, ".nfo")` if it exists, else (movie
  only) `movie.nfo` in the same folder.
- Show FOLDER `d`: `tvshow.nfo` in `d`.

### B4. Wire real metadata into catalog rows
- `ScanMovies`: title from `NfoReader.TryRead(NfoFor(file))?.Title` (+ `(Year)` when present) else
  the current `DefaultReleaseParser` title; `ThumbnailUrl = PosterUrl(ArtworkFinder.PosterFor(file))`.
- `ListShows`: title from `tvshow.nfo` `Title` else the parsed folder name; `ThumbnailUrl` from a
  show-folder poster.
- `DetailAsync` episodes: `Title = $"S{ss:D2}E{ee:D2}"` + `" - " + <episode nfo title>` when present;
  `ThumbnailUrl` from an episode poster if any (else null).
- `PosterUrl(path?)`: null when no poster, else `$"proxy/{Key}/{EncodeId(path)}/{Uri.EscapeDataString(Path.GetFileName(path))}"`
  — a relative proxy path served by `OpenAsync` (B6).

### B5. `MetaAsync`
```csharp
public Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
```
- A FILE id (`ResolveSafePath`): read its NFO (`NfoFor`); build `SourceDetail` with `Title`
  (nfo title or the derived filename title), `Overview` = plot, `ImageUrl` = `PosterUrl(poster)`,
  `Facts` = `[new MetaFact("Year", year)]` when a year is present. If no NFO and no poster, still
  return a `SourceDetail` with just the derived `Title` (so the panel shows the title, not blank),
  or null — **return null only when the id is invalid**; otherwise a title-only detail.
- A series FOLDER id (`ResolveSafeDir`): read `tvshow.nfo`; `Title`, `Overview` = plot, `ImageUrl` =
  show poster.
- An invalid id (neither a contained file nor a contained series dir) → null.

### B6. Serve images
Extend `MimeFor` with image arms: `.jpg`/`.jpeg` → `image/jpeg`, `.png` → `image/png`, `.webp` →
`image/webp` (keep the video arms; keep the doc note that the video-extension listing set and this
MIME map are peers). `OpenAsync` already serves any contained file with `MimeFor`'s type and Range,
so a poster id served through the proxy route just works — a poster is a contained file next to the
media. No new security surface (containment already covers it).

## Caching (deferred to Inc 4)

Inc 3 parses `.nfo` and probes artwork **on demand** during Search/Detail/Meta — consistent with
today's uncached filename parse. This multiplies per-browse I/O; the incremental mtime index (Inc 4)
is where a `FileResolverCache` under `IPluginContext.CacheDirectory`, keyed by path+mtime, caches
parsed results. Not built here (YAGNI for this increment; noted as the perf follow-up).

## Testing

**Host:**
- `AddonEndpoints` meta route: a stub `IMediaSource` returning a `SourceDetail{Title,Overview,ImageUrl,Facts}`
  → `/meta` JSON has `title/overview/image/facts:[{label,value}]` (empty-value facts dropped); a stub
  without `MetaAsync` → `{}`. (Use the existing route-test harness / a `WebApplicationFactory` path or
  call `AddonEndpoints.MetaAsync` with a stub router.)
- Version-pin tests → 1.13; compat theory gains `[InlineData(1, 12)]`.

**Plugin (extend `LocalLibrarySourceTests`, over temp dirs):**
- `NfoReader`: a valid `movie.nfo` (`<movie><title>…</title><year>1999</year><plot>…</plot></movie>`)
  → title/year/plot; an `<episodedetails>` and `<tvshow>` root each read title/plot; a malformed/
  empty/DTD-bearing `.nfo` → null (and the DTD one does NOT resolve any external entity — assert it
  returns null rather than throwing/expanding).
- `ArtworkFinder`: `<stem>-poster.jpg` preferred, then `poster.jpg`, then `folder.png`; none → null.
- Wire-in: a movie with a sidecar `.nfo` lists with the NFO title `(Year)` and a `ThumbnailUrl`
  pointing at `proxy/locallib/<id>/…`; a show with `tvshow.nfo` lists with the NFO title; an episode
  with an episode `.nfo` titles `S01E02 - <name>`.
- `MetaAsync`: a movie file id → `SourceDetail` with the plot as `Overview`, the poster as `ImageUrl`,
  and a `Year` fact; a series folder id → the show's `tvshow.nfo` overview/poster; an out-of-roots id
  → null.
- Image serving: `OpenAsync(posterId, null)` → 200 with `image/jpeg` and the poster bytes;
  containment still rejects an out-of-roots poster id.
- No test spawns a process, touches the network, or reads a real browser profile; the DTD/XXE test
  uses only a local temp file.

## What binds

- Host change is **additive**: a new optional interface method (default null), two new DTOs, one
  route swapped from a constant `{}` to a source-backed handler that still returns `{}` for any
  source not implementing `MetaAsync`. Single API bump 1.12 → 1.13. No existing source changes.
- The plugin reuses Inc 1/2 serving + security verbatim; a poster is a contained file, so no new
  security surface. Range still applies to images.
- **Cleanliness:** names no external content source; `RepositoryCleanlinessTests` stays green.
- The meta JSON shape matches the EverythingBox client's native `MediaDetail` parser exactly
  (`title/subtitle/overview/image/facts`, flat, not nested).
- Safe-XML: the `.nfo` reader prohibits DTDs, nulls the resolver, and caps entities — no XXE/entity
  expansion from an untrusted sidecar.

## Out of scope

- Background/fanart art (themed-only + needs absolute URLs a local file can't mint).
- Per-item caching / incremental mtime index (Inc 4).
- Music (Inc 4).
- Multi-image structures, ratings beyond a fact row, trailers, `imdbStreamId`/play-bridge.
- Watch state, transcoding, archive browsing (the issue's exclusions).

## Done when

- With `.nfo` sidecars and posters staged next to media, the library lists real titles/years and
  posters, and an item's detail panel shows its overview + poster + a Year fact — served entirely
  from local files, no network.
- `/meta` returns the client's flat detail shape for a source that implements `MetaAsync`, and `{}`
  for one that doesn't; API is 1.13; the `.nfo` reader is XXE-safe and tolerant.
- Movies (Inc 1) and series (Inc 2) still work; both engine test projects + the plugin tests green
  including `RepositoryCleanlinessTests`. Verified in Release.
