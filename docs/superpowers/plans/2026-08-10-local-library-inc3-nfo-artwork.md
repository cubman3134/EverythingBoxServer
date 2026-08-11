# Local Library Plugin — Increment 3 (.nfo + artwork) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read Kodi `.nfo` sidecars + local artwork so the LocalLibrary shows real titles/years/plots and posters — via an additive host meta-panel contract (`IMediaSource.MetaAsync` + `SourceDetail`) and plugin NFO/artwork readers.

**Architecture:** Part A (host) adds an optional `MetaAsync` returning `SourceDetail`, and makes the `/meta` route serialize the flat shape the EverythingBox client parses (`{title,subtitle,overview,image,facts}`). Part B (plugin) reads `.nfo` (XXE-safe) + finds posters, feeds real titles/posters to the catalog rows, implements `MetaAsync`, and serves images through the existing proxy/Range path.

**Tech Stack:** .NET 9 / C#, xUnit. One repo. `System.Xml`/`System.Xml.Linq` for NFO. No new NuGet package.

## Global Constraints

- **Additive host contract change, single API bump 1.12 → 1.13.** `MetaAsync` has a default (`null`) so every existing source is unaffected and `/meta` still returns `{}` for them.
- **The `/meta` JSON must be the client's FLAT shape:** `{ title, subtitle, overview, image, facts:[{label,value}] }` — NOT nested under a `meta` key. `overview` = plain-text plot; `image` = poster URL (relative `proxy/...` allowed); facts with empty values are dropped.
- **XXE safety:** the `.nfo` reader MUST prohibit DTDs, null the resolver, and cap entities. A DTD/entity-bearing `.nfo` must return null, never expand or fetch anything.
- **Plugin reuses Inc 1/2 serving + security verbatim.** A poster is a contained file — no new security surface; containment (`ResolveSafePath`) still gates every serve.
- **PUBLIC repo cleanliness:** no external content-source name in code/paths/tests/commit messages; `RepositoryCleanlinessTests` stays green.
- **Fresh-checkout-serves-nothing** unaffected (empty catalogs when unconfigured).
- Stage by explicit path (never `git add -A`); no AI attribution.
- No test spawns a process, touches the network, or reads a real browser profile — temp files only.
- Run tests per-project. **Task 1 (host, 1.13) must be committed before Task 4** (the plugin builds against the local host and implements `MetaAsync`).

---

### Task 1: Host — `MetaAsync` + `SourceDetail` + the `/meta` route (API 1.13)

**Files:**
- Create: `EverythingBox.Server.Abstractions/Metadata/SourceDetail.cs`
- Modify: `EverythingBox.Server.Abstractions/IMediaSource.cs` (add `MetaAsync` default)
- Modify: `EverythingBox.Server/AddonEndpoints.cs` (meta route + `MetaAsync` handler + `ToWireMeta`)
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` (1.12 → 1.13)
- Modify: `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` + `MetadataContractTests.cs` (Minor 13 + `[InlineData(1,12)]`)
- Test: `EverythingBox.Server.Tests/MetaRouteTests.cs`

**Interfaces:**
- Produces: `record MetaFact(string Label, string Value)`; `record SourceDetail(string Title, string? Subtitle, string? Overview, string? ImageUrl, IReadOnlyList<MetaFact>? Facts)`; `IMediaSource.MetaAsync(string, SourceContext, CancellationToken) → Task<SourceDetail?>` (default null); `internal static AddonEndpoints.MetaAsync(string type, string id, SourceRouter router, ILoggerFactory loggers, CancellationToken ct)`.

- [ ] **Step 1: Write the failing meta-route tests**

Create `EverythingBox.Server.Tests/MetaRouteTests.cs`. Build a stub `IMediaSource` and a real `SourceRouter` over it, call the handler, and assert the serialized value:
```csharp
using System.Text.Json;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class MetaRouteTests
{
    private sealed class RichSource : IMediaSource
    {
        public string Key => "rich";
        public IReadOnlyList<CatalogDescriptor> Catalogs => [];
        public Task<SourceCatalog> SearchAsync(string c, string? q, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceCatalog> DetailAsync(string i, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceStream?> ResolveAsync(string i, int n, SourceContext x, CancellationToken t) => Task.FromResult<SourceStream?>(null);
        public Task<SourceDetail?> MetaAsync(string i, SourceContext x, CancellationToken t) =>
            Task.FromResult<SourceDetail?>(new SourceDetail("Open Skies", "2024", "The synopsis.", "proxy/rich/abc/p.jpg",
                [new MetaFact("Year", "2024"), new MetaFact("Empty", "")]));
    }

    private sealed class BlankSource : IMediaSource
    {
        public string Key => "blank";
        public IReadOnlyList<CatalogDescriptor> Catalogs => [];
        public Task<SourceCatalog> SearchAsync(string c, string? q, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceCatalog> DetailAsync(string i, SourceContext x, CancellationToken t) => Task.FromResult(SourceCatalog.Empty("x"));
        public Task<SourceStream?> ResolveAsync(string i, int n, SourceContext x, CancellationToken t) => Task.FromResult<SourceStream?>(null);
        // no MetaAsync override → default null
    }

    private static string Serialize(IResult r) => JsonSerializer.Serialize(((IValueHttpResult)r).Value!);

    [Fact]
    public async Task Meta_route_emits_the_flat_detail_shape_for_a_rich_source()
    {
        var router = new SourceRouter([new RichSource()], NullLoggerFactory.Instance);
        var result = await AddonEndpoints.MetaAsync("movie", "rich:abc", router, NullLoggerFactory.Instance, default);
        var json = Serialize(result);
        Assert.Contains("\"title\":\"Open Skies\"", json);
        Assert.Contains("\"overview\":\"The synopsis.\"", json);
        Assert.Contains("\"image\":\"proxy/rich/abc/p.jpg\"", json);
        Assert.Contains("\"label\":\"Year\"", json);
        Assert.Contains("\"value\":\"2024\"", json);
        Assert.DoesNotContain("\"Empty\"", json); // empty-value fact dropped
    }

    [Fact]
    public async Task Meta_route_returns_empty_object_for_a_source_without_MetaAsync()
    {
        var router = new SourceRouter([new BlankSource()], NullLoggerFactory.Instance);
        var result = await AddonEndpoints.MetaAsync("movie", "blank:xyz", router, NullLoggerFactory.Instance, default);
        Assert.Equal("{}", Serialize(result));
    }
}
```
NOTE: confirm `SourceRouter`'s constructor signature and `IValueHttpResult` extraction against the codebase (Step 3 references them). If `SourceRouter`'s ctor differs, adapt the two `new SourceRouter(...)` calls to match; if `Results.Json` doesn't surface `IValueHttpResult`, execute the `IResult` against a `DefaultHttpContext` and read the response body instead — the assertions on the JSON stay the same.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~MetaRoute" -v minimal`
Expected: FAIL — `SourceDetail`/`MetaFact`/`MetaAsync`/`AddonEndpoints.MetaAsync` don't exist.

- [ ] **Step 3: Add the DTOs, the interface default, and the route**

Create `EverythingBox.Server.Abstractions/Metadata/SourceDetail.cs`:
```csharp
namespace EverythingBox.Server.Abstractions;

/// <summary>One labelled fact row on an item's detail panel (e.g. Year, Runtime).</summary>
public sealed record MetaFact(string Label, string Value);

/// <summary>
/// Rich per-item detail for the meta panel. <see cref="ImageUrl"/> may be a relative
/// <c>proxy/{key}/{id}/{name}</c> path (the client resolves it against the addon base). Text is plain.
/// </summary>
public sealed record SourceDetail(
    string Title,
    string? Subtitle = null,
    string? Overview = null,
    string? ImageUrl = null,
    IReadOnlyList<MetaFact>? Facts = null);
```

In `IMediaSource.cs`, add after `WarmUpAsync` (the default-method idiom):
```csharp
    /// <summary>Optional. Rich detail for one item's meta panel. Default: none — the meta route
    /// returns an empty object and the client shows a blank-but-valid panel.</summary>
    Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceDetail?>(null);
```

In `AddonEndpoints.cs`, replace the meta route line (currently the `(string type, string id) => Results.Json(new { })` lambda) with:
```csharp
        app.MapGet($"{prefix}/meta/{{type}}/{{id}}.json", MetaAsync);
```
and add the handler + serializer (next to `DetailAsync`/`ToWire`):
```csharp
    /// <summary>Same cancellation-vs-exception-type reasoning as <see cref="DetailAsync"/>.</summary>
    internal static async Task<IResult> MetaAsync(
        string type, string id, SourceRouter router, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!router.TryResolve(id, out var source, out var payload))
            return Results.Json(new { });
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
        image = d.ImageUrl,
        facts = (d.Facts ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => new { label = f.Label, value = f.Value })
            .ToArray(),
    };
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~MetaRoute" -v minimal`
Expected: PASS.

- [ ] **Step 5: Bump API + version-pin tests**

`ServerApi.VersionString` "1.12" → "1.13". In `ServerApiContractTests.cs` and `MetadataContractTests.cs`, update the version-pin test to Minor 13 (rename to `…_1_13_…now_that_sources_can_return_a_meta_panel`); add `[InlineData(1, 12)]` to the earlier-minor compat theory (keep existing rows).

- [ ] **Step 6: Full suites + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both green incl. `RepositoryCleanlinessTests`, the updated version tests, and any existing meta-route test (grep for a test asserting `/meta` returns `{}` — if one exists, update it to reflect the new default-null-still-`{}` behavior, which is unchanged for sources without `MetaAsync`).
```bash
git add EverythingBox.Server.Abstractions/Metadata/SourceDetail.cs EverythingBox.Server.Abstractions/IMediaSource.cs EverythingBox.Server/AddonEndpoints.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Tests/MetaRouteTests.cs
git commit -m "feat: sources can return a rich meta panel via MetaAsync (API 1.13)"
```

---

### Task 2: Plugin — `NfoReader` (XXE-safe, tolerant)

**Files:**
- Create: `EverythingBox.Server.LocalLibrary/NfoReader.cs`
- Test: `EverythingBox.Server.Tests/NfoReaderTests.cs`

**Interfaces:**
- Produces: `record NfoInfo(string? Title, int? Year, string? Plot)`; `internal static NfoInfo? NfoReader.TryRead(string nfoPath)`.

- [ ] **Step 1: Write the failing tests**

Create `EverythingBox.Server.Tests/NfoReaderTests.cs`:
```csharp
using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class NfoReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-nfo-" + Guid.NewGuid().ToString("N"));
    public NfoReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }

    private string Write(string name, string xml) { var p = Path.Combine(_dir, name); File.WriteAllText(p, xml); return p; }

    [Fact]
    public void Reads_movie_title_year_and_plot()
    {
        var p = Write("m.nfo", "<movie><title>The Matrix</title><year>1999</year><plot>A hacker learns the truth.</plot></movie>");
        var info = NfoReader.TryRead(p);
        Assert.NotNull(info);
        Assert.Equal("The Matrix", info!.Title);
        Assert.Equal(1999, info.Year);
        Assert.Equal("A hacker learns the truth.", info.Plot);
    }

    [Fact]
    public void Reads_episodedetails_and_tvshow_roots()
    {
        Assert.Equal("Pilot", NfoReader.TryRead(Write("e.nfo", "<episodedetails><title>Pilot</title><plot>It begins.</plot></episodedetails>"))!.Title);
        Assert.Equal("Some Show", NfoReader.TryRead(Write("t.nfo", "<tvshow><title>Some Show</title><plot>About a show.</plot></tvshow>"))!.Title);
    }

    [Fact]
    public void Malformed_or_missing_yields_null()
    {
        Assert.Null(NfoReader.TryRead(Write("bad.nfo", "<movie><title>oops")));
        Assert.Null(NfoReader.TryRead(Path.Combine(_dir, "does-not-exist.nfo")));
    }

    [Fact]
    public void A_DTD_entity_nfo_is_refused_without_expanding_it()
    {
        // If the reader honored the DTD it would either expand &xxe; or throw on the external ref.
        // Hardened settings must make this return null and never read the referenced file.
        var secret = Write("secret.txt", "TOPSECRET");
        var xml = $"<?xml version=\"1.0\"?><!DOCTYPE movie [<!ENTITY xxe SYSTEM \"file://{secret.Replace("\\","/")}\">]><movie><title>&xxe;</title></movie>";
        var info = NfoReader.TryRead(Write("xxe.nfo", xml));
        // Either null (DTD prohibited → throw → null) or a non-null with Title NOT containing the secret.
        Assert.True(info is null || !(info.Title ?? "").Contains("TOPSECRET"));
    }
}
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~NfoReader" -v minimal` → FAIL (no `NfoReader`).

- [ ] **Step 3: Implement `NfoReader`**

Create `EverythingBox.Server.LocalLibrary/NfoReader.cs`:
```csharp
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace EverythingBox.Server.LocalLibrary;

internal sealed record NfoInfo(string? Title, int? Year, string? Plot);

/// <summary>
/// Reads &lt;title&gt;/&lt;year&gt;/&lt;plot&gt; from a Kodi .nfo (movie / tvshow / episodedetails roots all
/// carry them). Namespace-agnostic. XXE-safe: DTDs prohibited, no external resolver, entities capped.
/// Tolerant: any failure (missing, malformed, I/O, disallowed DTD) → null.
/// </summary>
internal static class NfoReader
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static NfoInfo? TryRead(string nfoPath)
    {
        try
        {
            using var stream = File.OpenRead(nfoPath);
            using var reader = XmlReader.Create(stream, Settings);
            var doc = XDocument.Load(reader);

            string? First(string name) =>
                doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

            var title = First("title");
            var plot = First("plot");
            var year = int.TryParse(First("year"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : (int?)null;

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(plot) && year is null)
                return null;

            return new NfoInfo(
                string.IsNullOrWhiteSpace(title) ? null : title,
                year,
                string.IsNullOrWhiteSpace(plot) ? null : plot);
        }
        catch
        {
            return null; // missing / malformed / disallowed DTD / I/O — all non-fatal
        }
    }
}
```

- [ ] **Step 4: Run to verify pass.** Expected: PASS (incl. the XXE test).

- [ ] **Step 5: Commit**
```bash
git add EverythingBox.Server.LocalLibrary/NfoReader.cs EverythingBox.Server.Tests/NfoReaderTests.cs
git commit -m "feat: add an XXE-safe, tolerant Kodi .nfo reader"
```

---

### Task 3: Plugin — `ArtworkFinder` + image MIME types

**Files:**
- Create: `EverythingBox.Server.LocalLibrary/ArtworkFinder.cs`
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs` (`MimeFor` image arms)
- Test: `EverythingBox.Server.Tests/ArtworkFinderTests.cs`

**Interfaces:**
- Produces: `internal static string? ArtworkFinder.PosterFor(string mediaFileOrDir)`.

- [ ] **Step 1: Write the failing tests**

Create `EverythingBox.Server.Tests/ArtworkFinderTests.cs`:
```csharp
using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class ArtworkFinderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ebs-art-" + Guid.NewGuid().ToString("N"));
    public ArtworkFinderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } GC.SuppressFinalize(this); }
    private string Touch(string name) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, [0]); return p; }

    [Fact]
    public void Prefers_a_companion_poster_next_to_the_file()
    {
        var movie = Touch("The Matrix (1999).mkv");
        Touch("poster.jpg");
        var companion = Touch("The Matrix (1999)-poster.jpg");
        Assert.Equal(companion, ArtworkFinder.PosterFor(movie));
    }

    [Fact]
    public void Falls_back_to_folder_level_poster_then_folder_image()
    {
        var movie = Touch("Film.mkv");
        var folderPoster = Touch("poster.png");
        Assert.Equal(folderPoster, ArtworkFinder.PosterFor(movie));
    }

    [Fact]
    public void A_directory_uses_its_folder_poster()
    {
        var show = Path.Combine(_dir, "Show"); Directory.CreateDirectory(show);
        var p = Path.Combine(show, "folder.webp"); File.WriteAllBytes(p, [0]);
        Assert.Equal(p, ArtworkFinder.PosterFor(show));
    }

    [Fact]
    public void No_art_yields_null() => Assert.Null(ArtworkFinder.PosterFor(Touch("Lonely.mkv")));
}
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement `ArtworkFinder` + extend `MimeFor`**

Create `EverythingBox.Server.LocalLibrary/ArtworkFinder.cs`:
```csharp
namespace EverythingBox.Server.LocalLibrary;

/// <summary>Locates a poster image for a media file or a show folder, using Kodi/Jellyfin naming.</summary>
internal static class ArtworkFinder
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] FolderBaseNames = ["poster", "folder"];

    public static string? PosterFor(string mediaFileOrDir)
    {
        if (Directory.Exists(mediaFileOrDir))
            return FolderPoster(mediaFileOrDir);

        var dir = Path.GetDirectoryName(mediaFileOrDir);
        if (dir is null) return null;

        // 1) "<stem>-poster.<img>" next to the file.
        var stem = Path.GetFileNameWithoutExtension(mediaFileOrDir);
        foreach (var ext in ImageExtensions)
        {
            var companion = Path.Combine(dir, stem + "-poster" + ext);
            if (File.Exists(companion)) return companion;
        }
        // 2) "poster.*" / "folder.*" in the same directory.
        return FolderPoster(dir);
    }

    private static string? FolderPoster(string dir)
    {
        foreach (var baseName in FolderBaseNames)
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, baseName + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}
```

In `LocalLibrarySource.MimeFor`, add image arms before the default (and update the peer-comment to note images are served too):
```csharp
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
```

- [ ] **Step 4: Run to verify pass.**

- [ ] **Step 5: Commit**
```bash
git add EverythingBox.Server.LocalLibrary/ArtworkFinder.cs EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.Tests/ArtworkFinderTests.cs
git commit -m "feat: locate local poster art and serve images through the proxy route"
```

---

### Task 4: Plugin — wire real titles/posters into rows + `MetaAsync`

**Files:**
- Modify: `EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs`
- Test: `EverythingBox.Server.Tests/LocalLibrarySourceTests.cs` (append)

**Interfaces:**
- Consumes: `NfoReader.TryRead` (Task 2), `ArtworkFinder.PosterFor` (Task 3), `SourceDetail`/`MetaFact`/`MetaAsync` (Task 1), existing `EncodeId`/`ResolveSafePath`/`ResolveSafeDir`/`MimeFor`.

- [ ] **Step 1: Write the failing tests**

Append to `LocalLibrarySourceTests` (uses the existing `_root`, `Movies(...)`, `Series(...)`, `Ctx()`, `MakeShow()` helpers). Add helpers to write NFO/posters, then:
```csharp
    [Fact]
    public async Task Movie_row_uses_the_nfo_title_and_a_poster_thumbnail()
    {
        var mkv = Path.Combine(_root, "generic.mkv");
        File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real Title</title><year>2011</year><plot>P.</plot></movie>");
        File.WriteAllBytes(Path.Combine(_root, "generic-poster.jpg"), [9]);

        var item = Assert.Single((await Movies().SearchAsync("movies", "real", Ctx(), default)).Items);
        Assert.Equal("Real Title (2011)", item.Title);
        Assert.StartsWith("proxy/locallib/", item.ThumbnailUrl);
    }

    [Fact]
    public async Task MetaAsync_returns_overview_poster_and_year_for_a_movie()
    {
        var mkv = Path.Combine(_root, "generic.mkv");
        File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_root, "generic.nfo"), "<movie><title>Real Title</title><year>2011</year><plot>The plot.</plot></movie>");
        File.WriteAllBytes(Path.Combine(_root, "generic-poster.jpg"), [9]);

        var id = LocalLibrarySource.EncodeId(mkv);
        var detail = await Movies().MetaAsync(id, Ctx(), default);
        Assert.NotNull(detail);
        Assert.Equal("Real Title", detail!.Title);
        Assert.Equal("The plot.", detail.Overview);
        Assert.StartsWith("proxy/locallib/", detail.ImageUrl);
        Assert.Contains(detail.Facts!, f => f.Label == "Year" && f.Value == "2011");
    }

    [Fact]
    public async Task MetaAsync_on_a_series_folder_reads_tvshow_nfo()
    {
        var (seriesRoot, showDir) = MakeShow();
        File.WriteAllText(Path.Combine(showDir, "tvshow.nfo"), "<tvshow><title>Breaking Show</title><plot>Show plot.</plot></tvshow>");
        var showId = (await Series(seriesRoot).SearchAsync("series", null, Ctx(), default)).Items.Single().Id;
        var detail = await Series(seriesRoot).MetaAsync(showId, Ctx(), default);
        Assert.Equal("Show plot.", detail!.Overview);
    }

    [Fact]
    public async Task MetaAsync_on_an_out_of_roots_id_is_null()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-out-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllBytes(outside, [1]);
        try { Assert.Null(await Movies().MetaAsync(LocalLibrarySource.EncodeId(outside), Ctx(), default)); }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task Episode_uses_the_episode_nfo_title()
    {
        var (seriesRoot, showDir) = MakeShow();
        var seasonDir = Path.Combine(showDir, "Season 01");
        File.WriteAllText(Path.Combine(seasonDir, "Breaking.Show.S01E01.nfo"), "<episodedetails><title>Pilot</title></episodedetails>");
        var showId = (await Series(seriesRoot).SearchAsync("series", null, Ctx(), default)).Items.Single().Id;
        var eps = await Series(seriesRoot).DetailAsync(showId, Ctx(), default);
        Assert.Equal("S01E01 - Pilot", eps.Items[0].Title);
    }

    [Fact]
    public async Task A_poster_id_serves_as_an_image()
    {
        var mkv = Path.Combine(_root, "img.mkv"); File.WriteAllBytes(mkv, [1, 2, 3, 4]);
        var poster = Path.Combine(_root, "img-poster.png"); File.WriteAllBytes(poster, [7, 7, 7]);
        var id = LocalLibrarySource.EncodeId(poster);
        await using var r = await Movies().OpenAsync(id, null, default);
        Assert.NotNull(r);
        Assert.Equal(200, r!.StatusCode);
        Assert.Equal("image/png", r.ContentType);
    }
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Wire NFO/artwork into the source**

In `LocalLibrarySource.cs`:
- Add helpers:
```csharp
    private static readonly string[] NfoExt = [".nfo"];

    // The .nfo for a media FILE: "<stem>.nfo" sidecar, else "movie.nfo" in the same folder.
    private static string? MovieNfo(string file)
    {
        var sidecar = Path.ChangeExtension(file, ".nfo");
        if (sidecar is not null && File.Exists(sidecar)) return sidecar;
        var dir = Path.GetDirectoryName(file);
        var movieNfo = dir is null ? null : Path.Combine(dir, "movie.nfo");
        return movieNfo is not null && File.Exists(movieNfo) ? movieNfo : null;
    }

    private string? PosterUrl(string? posterPath) => posterPath is null
        ? null
        : $"proxy/{Key}/{EncodeId(posterPath)}/{Uri.EscapeDataString(Path.GetFileName(posterPath))}";
```
- In `ScanMovies`, replace the `title`/`Id`/item construction to prefer the NFO title and attach a poster:
```csharp
                var nfo = MovieNfo(path) is { } np ? NfoReader.TryRead(np) : null;
                var title = nfo?.Title is { } nt
                    ? (nfo.Year is { } ny ? $"{nt} ({ny})" : nt)
                    : TitleFor(parser, path);

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(
                    Id: EncodeId(path), Title: title,
                    Subtitle: Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty,
                    MediaType: "movie",
                    ThumbnailUrl: PosterUrl(ArtworkFinder.PosterFor(path)),
                    Expandable: false));
```
- In `ListShows`, prefer `tvshow.nfo` title + a folder poster:
```csharp
                var tvshow = Path.Combine(dir, "tvshow.nfo");
                var nfo = File.Exists(tvshow) ? NfoReader.TryRead(tvshow) : null;
                var title = nfo?.Title ?? ShowTitle(parser, dir);   // ShowTitle: the existing parsed-name helper
                ...
                items.Add(new CatalogItem(Id: EncodeId(dir), Title: title, Subtitle: string.Empty,
                    MediaType: "series", ThumbnailUrl: PosterUrl(ArtworkFinder.PosterFor(dir)), Expandable: true));
```
  (If Inc 2 inlined the show-title derivation instead of a `ShowTitle` helper, extract it to `ShowTitle(parser, dir)` now and call it from both places.)
- In `DetailAsync`'s episode loop, append the episode NFO title:
```csharp
            var epNfo = File.Exists(Path.ChangeExtension(path, ".nfo")) ? NfoReader.TryRead(Path.ChangeExtension(path, ".nfo")) : null;
            var epTitle = $"S{season:D2}E{episode:D2}" + (epNfo?.Title is { } et ? $" - {et}" : "");
            ...
                Title: epTitle,
```

- [ ] **Step 4: Implement `MetaAsync`**
```csharp
    public Task<SourceDetail?> MetaAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        // A file id (movie/episode) → its own .nfo; a series folder id → tvshow.nfo.
        if (ResolveSafePath(itemId) is { } file)
        {
            var nfo = MovieNfo(file) is { } np ? NfoReader.TryRead(np) : null;
            var title = nfo?.Title ?? TitleFor(new DefaultReleaseParser(), file);
            var facts = nfo?.Year is { } y ? new[] { new MetaFact("Year", y.ToString()) } : [];
            return Task.FromResult<SourceDetail?>(new SourceDetail(
                Title: title, Overview: nfo?.Plot, ImageUrl: PosterUrl(ArtworkFinder.PosterFor(file)), Facts: facts));
        }
        if (ResolveSafeDir(itemId) is { } dir)
        {
            var tvshow = Path.Combine(dir, "tvshow.nfo");
            var nfo = File.Exists(tvshow) ? NfoReader.TryRead(tvshow) : null;
            var title = nfo?.Title ?? ShowTitle(new DefaultReleaseParser(), dir);
            return Task.FromResult<SourceDetail?>(new SourceDetail(
                Title: title, Overview: nfo?.Plot, ImageUrl: PosterUrl(ArtworkFinder.PosterFor(dir))));
        }
        return Task.FromResult<SourceDetail?>(null);
    }
```
(`TitleFor` and `ShowTitle` are the existing/extracted filename-title helpers; reuse them.)

- [ ] **Step 5: Run to verify pass** — `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~LocalLibrarySource" -v minimal` → PASS.

- [ ] **Step 6: Full suites + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal` — both green incl. `RepositoryCleanlinessTests`, `SampleSourceTests`, and the Inc 1/2 tests.
```bash
git add EverythingBox.Server.LocalLibrary/LocalLibrarySource.cs EverythingBox.Server.Tests/LocalLibrarySourceTests.cs
git commit -m "feat: surface .nfo titles/plots and posters, and a rich meta panel, for the local library"
```
(Optionally update `EverythingBox.Server.LocalLibrary/README.md` to mention `.nfo`/artwork support; stage it if you do.)

---

## Self-review

**Spec coverage:** Host DTOs + `MetaAsync` default + meta route flat shape + API 1.13 (Part A) → Task 1. `NfoReader` XXE-safe/tolerant (B1) → Task 2. `ArtworkFinder` + image MIME (B2/B6) → Task 3. NFO titles/years on rows, posters → `ThumbnailUrl`, episode titles, `MetaAsync` impl (B3/B4/B5) → Task 4. Caching deferred, background out of scope → no task adds them. ✅

**Placeholder scan:** none — all code shown; the one "confirm SourceRouter ctor / IValueHttpResult" note in Task 1 Step 1 is a verify-against-codebase instruction with a concrete fallback, not a placeholder.

**Type consistency:** `SourceDetail(Title, Subtitle?, Overview?, ImageUrl?, Facts?)` + `MetaFact(Label, Value)` produced in Task 1, consumed in Task 4's `MetaAsync` and the meta test. `NfoInfo(Title, Year, Plot)` + `NfoReader.TryRead` produced Task 2, used Task 4. `ArtworkFinder.PosterFor` produced Task 3, used Task 4 + `PosterUrl`. `MetaAsync` signature identical in interface (Task 1) and impl (Task 4). Version `"1.13"`/Minor 13/`[InlineData(1,12)]` consistent.
