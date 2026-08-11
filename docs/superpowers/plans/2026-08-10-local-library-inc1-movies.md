# Local Library Plugin — Increment 1 (Movies + Range) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new in-repo `EverythingBox.Server.LocalLibrary` plugin that lists a user's configured movie folders in a `movies` catalog and serves each file with correct HTTP Range (seeking).

**Architecture:** A pure `RangeRequest.Parse` + a `BoundedReadStream` implement byte-range slicing; `MovieLibrarySource : IMediaSource` scans configured roots, classifies via `DefaultReleaseParser` + `MediaFileMatcher.VideoExtensions`, enforces path-containment security mirroring `SampleSource`, and serves via `OpenAsync`. The plugin is loaded only from `plugins/locallib/`, referenced only by the Tests project, so a fresh checkout serves nothing.

**Tech Stack:** .NET 9 / C#, xUnit. New project references only `EverythingBox.Server.Abstractions` (`Private="false"`). No host/contract change, no API bump, no new NuGet package.

## Global Constraints

- **New in-repo plugin only.** `EverythingBox.Server` gains NO reference to it. `ServerApi.VersionString` is UNCHANGED — no contract/API change.
- **Fresh-checkout-serves-nothing holds:** unconfigured → `MovieLibrarySource.Catalogs` is empty; unstaged → `PluginHost` never loads it.
- **Path security is mandatory.** Every `ResolveAsync`/`OpenAsync` re-decodes the id, resolves its real path (junctions/symlinks), and confirms containment in a configured root — an id escaping the roots serves nothing. Mirror `EverythingBox.Server.SampleSource/LocalFolderSource.cs`'s `ResolveReal`/`IsContained`/`ResolvePath` discipline EXACTLY; adversarial tests are the gate.
- **PUBLIC repo cleanliness:** the plugin names no external content source; keep it that way (no denylisted term in code, paths, tests, or commit messages). `RepositoryCleanlinessTests` stays green.
- Plugin key is `"locallib"` (distinct from SampleSource's `"local"`). Catalog/item media type is the protocol string `"movie"`.
- No `FileStream` leak per request — the opened stream is disposed through `ProxyResponse.DisposeAsync` (host calls `await upstream.DisposeAsync()`), so `BoundedReadStream` disposes its inner `FileStream`, and the Full (200) body IS the `FileStream`.
- Stage files by explicit path (never `git add -A`). No AI attribution in any commit.
- No test spawns a process, touches the network, or reads a real browser profile. Tests use only temp dirs they create.
- Run tests per-project (this CLI rejects two projects in one `dotnet test` call). The plugin's tests live in `EverythingBox.Server.Tests`.

---

### Task 1: Scaffold the project + `RangeRequest.Parse`

**Files:**
- Create: `EverythingBox.Server.LocalLibrary/EverythingBox.Server.LocalLibrary.csproj`
- Create: `EverythingBox.Server.LocalLibrary/RangeRequest.cs`
- Modify: `EverythingBoxServer.sln` (add the project) and `EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj` (reference it)
- Test: `EverythingBox.Server.Tests/RangeRequestTests.cs`

**Interfaces:**
- Produces: `EverythingBox.Server.LocalLibrary` namespace; `internal enum RangeKind { Full, Partial, Unsatisfiable }`; `internal readonly record struct RangeResult(RangeKind Kind, long Start, long Length)`; `internal static class RangeRequest { static RangeResult Parse(string? header, long totalLength) }`.

- [ ] **Step 1: Create the project + sln + test reference**

Create `EverythingBox.Server.LocalLibrary/EverythingBox.Server.LocalLibrary.csproj` (mirror `EverythingBox.Server.SampleSource.csproj`):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <InternalsVisibleTo Include="EverythingBox.Server.Tests" />
  </PropertyGroup>
  <ItemGroup>
    <!-- The host supplies Abstractions at load time; shipping a second copy makes every cast fail. -->
    <ProjectReference Include="..\EverythingBox.Server.Abstractions\EverythingBox.Server.Abstractions.csproj" Private="false" />
  </ItemGroup>
</Project>
```
(If `InternalsVisibleTo` as an item isn't already the repo's idiom, use an `AssemblyAttribute` instead — check how `EverythingBox.Server.csproj` exposes internals to tests and match it.)

Add to the solution and wire the test reference:
```bash
dotnet sln EverythingBoxServer.sln add EverythingBox.Server.LocalLibrary/EverythingBox.Server.LocalLibrary.csproj
```
In `EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj`, add (next to the existing SampleSource reference):
```xml
    <ProjectReference Include="..\EverythingBox.Server.LocalLibrary\EverythingBox.Server.LocalLibrary.csproj" />
```

- [ ] **Step 2: Write the failing `RangeRequest` tests**

Create `EverythingBox.Server.Tests/RangeRequestTests.cs`:
```csharp
using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class RangeRequestTests
{
    [Fact] public void No_header_is_Full()            => AssertFull(RangeRequest.Parse(null, 5000));
    [Fact] public void Empty_header_is_Full()         => AssertFull(RangeRequest.Parse("", 5000));
    [Fact] public void Wrong_unit_is_Full()           => AssertFull(RangeRequest.Parse("items=0-1", 5000));
    [Fact] public void Malformed_is_Full()            => AssertFull(RangeRequest.Parse("bytes=abc", 5000));
    [Fact] public void Multi_range_is_Full()          => AssertFull(RangeRequest.Parse("bytes=0-1,2-3", 5000));
    [Fact] public void Start_after_end_is_Full()      => AssertFull(RangeRequest.Parse("bytes=100-50", 5000));
    [Fact] public void Zero_total_is_Full()           => AssertFull(RangeRequest.Parse("bytes=0-10", 0));

    [Fact]
    public void Closed_range_is_Partial()
    {
        var r = RangeRequest.Parse("bytes=0-1023", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(0, r.Start); Assert.Equal(1024, r.Length);
    }

    [Fact]
    public void Open_ended_range_runs_to_EOF()
    {
        var r = RangeRequest.Parse("bytes=1024-", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(1024, r.Start); Assert.Equal(3976, r.Length);
    }

    [Fact]
    public void Suffix_range_is_the_last_N_bytes()
    {
        var r = RangeRequest.Parse("bytes=-500", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(4500, r.Start); Assert.Equal(500, r.Length);
    }

    [Fact]
    public void Closed_end_past_EOF_is_clamped()
    {
        var r = RangeRequest.Parse("bytes=4999-999999", 5000);
        Assert.Equal(RangeKind.Partial, r.Kind); Assert.Equal(4999, r.Start); Assert.Equal(1, r.Length);
    }

    [Fact] public void Start_at_total_is_Unsatisfiable()  => Assert.Equal(RangeKind.Unsatisfiable, RangeRequest.Parse("bytes=5000-", 5000).Kind);
    [Fact] public void Start_past_total_is_Unsatisfiable() => Assert.Equal(RangeKind.Unsatisfiable, RangeRequest.Parse("bytes=6000-7000", 5000).Kind);

    private static void AssertFull(RangeResult r) => Assert.Equal(RangeKind.Full, r.Kind);
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~RangeRequest" -v minimal`
Expected: FAIL — `RangeRequest`/`RangeKind`/`RangeResult` don't exist (build error).

- [ ] **Step 4: Implement `RangeRequest`**

Create `EverythingBox.Server.LocalLibrary/RangeRequest.cs`:
```csharp
using System.Globalization;

namespace EverythingBox.Server.LocalLibrary;

internal enum RangeKind { Full, Partial, Unsatisfiable }

internal readonly record struct RangeResult(RangeKind Kind, long Start, long Length)
{
    public static readonly RangeResult Full = new(RangeKind.Full, 0, 0);
    public static readonly RangeResult Unsatisfiable = new(RangeKind.Unsatisfiable, 0, 0);
    public static RangeResult Partial(long start, long length) => new(RangeKind.Partial, start, length);
}

/// <summary>
/// Parses a single HTTP byte-range header against a known total length. Anything it does not
/// understand (no header, wrong unit, multiple ranges, garbage, or a start after the end) degrades
/// to <see cref="RangeKind.Full"/> — serve the whole file (200) — rather than erroring.
/// </summary>
internal static class RangeRequest
{
    public static RangeResult Parse(string? header, long totalLength)
    {
        if (totalLength <= 0 || string.IsNullOrWhiteSpace(header))
            return RangeResult.Full;

        var trimmed = header.Trim();
        const string prefix = "bytes=";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return RangeResult.Full;

        var spec = trimmed[prefix.Length..];
        if (spec.Contains(','))            // multi-range: not worth it for media → serve whole
            return RangeResult.Full;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return RangeResult.Full;

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];

        // Suffix form "-N": the last N bytes.
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                return RangeResult.Full;
            var start = Math.Max(0, totalLength - suffix);
            return RangeResult.Partial(start, totalLength - start);
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var from) || from < 0)
            return RangeResult.Full;

        if (from >= totalLength)
            return RangeResult.Unsatisfiable;

        // Open-ended "start-": to EOF.
        if (endText.Length == 0)
            return RangeResult.Partial(from, totalLength - from);

        // Closed "start-end".
        if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var to) || to < 0)
            return RangeResult.Full;
        if (to < from)
            return RangeResult.Full;

        var last = Math.Min(to, totalLength - 1);
        return RangeResult.Partial(from, last - from + 1);
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~RangeRequest" -v minimal`
Expected: PASS — all cases green.

- [ ] **Step 6: Commit**

```bash
git add EverythingBox.Server.LocalLibrary/EverythingBox.Server.LocalLibrary.csproj EverythingBox.Server.LocalLibrary/RangeRequest.cs EverythingBoxServer.sln EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj EverythingBox.Server.Tests/RangeRequestTests.cs
git commit -m "feat: scaffold a local-library plugin and add a byte-range header parser"
```

---

### Task 2: `BoundedReadStream`

**Files:**
- Create: `EverythingBox.Server.LocalLibrary/BoundedReadStream.cs`
- Test: `EverythingBox.Server.Tests/BoundedReadStreamTests.cs`

**Interfaces:**
- Produces: `internal sealed class BoundedReadStream(Stream inner, long length) : Stream` — read-only, yields at most `length` bytes from `inner` (already positioned at the slice start), disposes `inner` on dispose.

- [ ] **Step 1: Write the failing tests**

Create `EverythingBox.Server.Tests/BoundedReadStreamTests.cs`:
```csharp
using EverythingBox.Server.LocalLibrary;

namespace EverythingBox.Server.Tests;

public class BoundedReadStreamTests
{
    private sealed class DisposeProbe(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task Yields_exactly_the_bounded_slice()
    {
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
        var inner = new MemoryStream(data);
        inner.Seek(10, SeekOrigin.Begin);

        using var bounded = new BoundedReadStream(inner, 20);
        using var sink = new MemoryStream();
        await bounded.CopyToAsync(sink);

        var got = sink.ToArray();
        Assert.Equal(20, got.Length);
        Assert.Equal(Enumerable.Range(10, 20).Select(i => (byte)i), got); // bytes [10,30)
    }

    [Fact]
    public async Task Disposing_disposes_the_inner_stream()
    {
        var probe = new DisposeProbe(new byte[50]);
        var bounded = new BoundedReadStream(probe, 10);
        await bounded.DisposeAsync();
        Assert.True(probe.Disposed);
    }

    [Fact]
    public void Reports_the_bounded_length_and_is_read_only()
    {
        using var bounded = new BoundedReadStream(new MemoryStream(new byte[50]), 10);
        Assert.Equal(10, bounded.Length);
        Assert.True(bounded.CanRead);
        Assert.False(bounded.CanWrite);
        Assert.False(bounded.CanSeek);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~BoundedReadStream" -v minimal`
Expected: FAIL — `BoundedReadStream` doesn't exist.

- [ ] **Step 3: Implement `BoundedReadStream`**

Create `EverythingBox.Server.LocalLibrary/BoundedReadStream.cs`:
```csharp
namespace EverythingBox.Server.LocalLibrary;

/// <summary>
/// A read-only view over <paramref name="inner"/> (already positioned at the slice start) that
/// yields at most <paramref name="length"/> bytes, then reports end-of-stream. Owns <paramref name="inner"/>:
/// disposing this disposes it. Used to serve one HTTP byte-range slice of a file.
/// </summary>
internal sealed class BoundedReadStream(Stream inner, long length) : Stream
{
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, _remaining);
        var read = inner.Read(buffer, offset, toRead);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0) return 0;
        var slice = buffer.Length <= _remaining ? buffer : buffer[..(int)_remaining];
        var read = await inner.ReadAsync(slice, cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~BoundedReadStream" -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add EverythingBox.Server.LocalLibrary/BoundedReadStream.cs EverythingBox.Server.Tests/BoundedReadStreamTests.cs
git commit -m "feat: add a bounded read-only stream for serving one byte-range slice"
```

---

### Task 3: `MovieLibrarySource` — scan, classify, resolve, path security + the plugin

**Files:**
- Create: `EverythingBox.Server.LocalLibrary/LocalLibraryConfig.cs`
- Create: `EverythingBox.Server.LocalLibrary/LocalLibraryPlugin.cs`
- Create: `EverythingBox.Server.LocalLibrary/MovieLibrarySource.cs` (everything except `OpenAsync`, which returns the interface default `null` until Task 4)
- Test: `EverythingBox.Server.Tests/MovieLibrarySourceTests.cs`

**Interfaces:**
- Consumes: `IMediaSource`, `CatalogDescriptor`, `CatalogItem`, `SourceCatalog`, `SourceStream`, `SourceContext` (Abstractions); `DefaultReleaseParser`, `MediaFileMatcher.VideoExtensions`, `MediaType.Movie`.
- Produces: `MovieLibrarySource(IReadOnlyList<string> movieRoots, ILogger logger)` with `Catalogs`, `SearchAsync`, `DetailAsync`, `ResolveAsync`, and internal `EncodeId`/`ResolveSafePath` used by Task 4.

**Before writing:** READ `EverythingBox.Server.SampleSource/LocalFolderSource.cs` in full — specifically its `EncodeId`/`DecodeId`, `ResolvePath`/`ResolveReal`, and `IsContained` methods and its `EnumerationOptions`. Replicate that path-security discipline EXACTLY (adapted to the `movieRoots`); it is security-critical and must not be re-derived loosely.

- [ ] **Step 1: Write the failing tests**

Create `EverythingBox.Server.Tests/MovieLibrarySourceTests.cs`. Build a temp dir with two videos and a non-video, construct the source over it, and assert scan/classify/query/resolve/containment:
```csharp
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.LocalLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

public class MovieLibrarySourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ebs-lib-" + Guid.NewGuid().ToString("N"));

    public MovieLibrarySourceTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "Some.Movie.2019.1080p.BluRay.mkv"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(_root, "Another Film (2020).mp4"), new byte[] { 5, 6, 7, 8 });
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not a movie");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } GC.SuppressFinalize(this); }

    private MovieLibrarySource Source(params string[] roots)
        => new(roots.Length == 0 ? [_root] : roots, NullLogger<MovieLibrarySource>.Instance);

    private static SourceContext Ctx() => new();

    [Fact]
    public void No_configured_roots_declares_no_catalog()
        => Assert.Empty(new MovieLibrarySource([], NullLogger<MovieLibrarySource>.Instance).Catalogs);

    [Fact]
    public void A_configured_root_declares_the_movies_catalog()
    {
        var c = Assert.Single(Source().Catalogs);
        Assert.Equal("movies", c.Id);
        Assert.Equal("movie", c.MediaType);
    }

    [Fact]
    public async Task Scans_only_video_files_and_titles_them_from_the_filename()
    {
        var catalog = await Source().SearchAsync("movies", null, Ctx(), default);
        Assert.Equal(2, catalog.Items.Count);
        Assert.All(catalog.Items, i => Assert.Equal("movie", i.MediaType));
        Assert.All(catalog.Items, i => Assert.False(i.Expandable));
        Assert.Contains(catalog.Items, i => i.Title == "Some Movie (2019)");
        Assert.Contains(catalog.Items, i => i.Title == "Another Film (2020)");
        Assert.DoesNotContain(catalog.Items, i => i.Title.Contains("notes"));
    }

    [Fact]
    public async Task A_query_filters_by_title()
    {
        var catalog = await Source().SearchAsync("movies", "another", Ctx(), default);
        var item = Assert.Single(catalog.Items);
        Assert.Equal("Another Film (2020)", item.Title);
    }

    [Fact]
    public async Task Resolve_returns_a_proxy_url_for_a_real_item()
    {
        var item = (await Source().SearchAsync("movies", "some", Ctx(), default)).Items.Single();
        var stream = await Source().ResolveAsync(item.Id, 0, Ctx(), default);
        Assert.NotNull(stream);
        Assert.StartsWith("proxy/locallib/", stream!.Url);
    }

    [Fact]
    public async Task An_id_whose_path_escapes_the_roots_resolves_to_nothing()
    {
        // An id for a real file OUTSIDE the configured roots must not resolve.
        var outside = Path.Combine(Path.GetTempPath(), "ebs-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            var evilId = MovieLibrarySource.EncodeId(outside);       // internal, visible to tests
            var src = new MovieLibrarySource([_root], NullLogger<MovieLibrarySource>.Instance);
            Assert.Null(await src.ResolveAsync(evilId, 0, Ctx(), default));
        }
        finally { File.Delete(outside); }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~MovieLibrarySource" -v minimal`
Expected: FAIL — the types don't exist.

- [ ] **Step 3: Implement config + plugin + source**

Create `LocalLibraryConfig.cs`:
```csharp
namespace EverythingBox.Server.LocalLibrary;

public sealed class LocalLibraryConfig
{
    /// <summary>Absolute paths to folders whose video files are treated as movies.</summary>
    public List<string> Movies { get; set; } = [];
}
```

Create `LocalLibraryPlugin.cs`:
```csharp
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.LocalLibrary;

public sealed class LocalLibraryPlugin : IPlugin
{
    public string Key => "locallib";
    public string DisplayName => "Local Library";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<LocalLibraryConfig>() ?? new LocalLibraryConfig();
        registry.AddSource(new MovieLibrarySource(config.Movies, context.Loggers.CreateLogger<MovieLibrarySource>()));
    }
}
```
(Match the exact `IPlugin`/`IPluginRegistry`/`IPluginContext` member names against `SampleSource/SamplePlugin.cs` — mirror its `Configure` signature and `AddSource` call precisely.)

Create `MovieLibrarySource.cs` implementing `IMediaSource` — `Key => "locallib"`; `Catalogs` empty when `movieRoots` is empty else `[new CatalogDescriptor("movies", "Movies", "movie")]`; `SearchAsync` walking each existing root with `EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }`, keeping files whose extension is in `MediaFileMatcher.VideoExtensions`, titling via `new DefaultReleaseParser().Parse(Path.GetFileNameWithoutExtension(path), MediaType.Movie)` (`NormalizedTitle`, fallback to the stem; append `" (" + Year + ")"` when `Year` is set), `Subtitle` = parent-folder name, `Id = EncodeId(fullPath)`, `MediaType = "movie"`, `Expandable = false`; filter by `query` (case-insensitive `Title.Contains`) when non-null; order by `Title` (OrdinalIgnoreCase); cap at 5000 and set `HasMore` if capped. `DetailAsync` → `SourceCatalog.Empty("Movies")`. `ResolveAsync` → `ResolveSafePath(itemId)` then `new SourceStream($"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}", MimeFor(path))`, or null. Leave `OpenAsync` as the interface default (returns null) for now — Task 4 adds it.

`internal static string EncodeId(string absolutePath)` / `internal static string? TryDecodeId(string id)` — base64url of the UTF-8 path (mirror `LocalFolderSource`). `private string? ResolveSafePath(string itemId)` — decode; real-resolve; confirm containment in one of `movieRoots` (real-resolved); return null on any failure. Replicate `LocalFolderSource.ResolveReal`/`IsContained` exactly. Add a placeholder `MimeFor(path)` returning `"video/x-matroska"` for `.mkv`, `"video/mp4"` for `.mp4`, else `"application/octet-stream"` (Task 4 expands it).

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~MovieLibrarySource" -v minimal`
Expected: PASS — scan/classify/query/resolve/containment all green.

- [ ] **Step 5: Commit**

```bash
git add EverythingBox.Server.LocalLibrary/LocalLibraryConfig.cs EverythingBox.Server.LocalLibrary/LocalLibraryPlugin.cs EverythingBox.Server.LocalLibrary/MovieLibrarySource.cs EverythingBox.Server.Tests/MovieLibrarySourceTests.cs
git commit -m "feat: scan and classify local movie files with path-containment security"
```

---

### Task 4: `MovieLibrarySource.OpenAsync` — Range serving + MIME + README

**Files:**
- Modify: `EverythingBox.Server.LocalLibrary/MovieLibrarySource.cs` (add `OpenAsync`, expand `MimeFor`)
- Create: `EverythingBox.Server.LocalLibrary/README.md`
- Test: `EverythingBox.Server.Tests/MovieLibrarySourceTests.cs` (append serving tests)

**Interfaces:**
- Consumes: `RangeRequest.Parse` (Task 1), `BoundedReadStream` (Task 2), `ResolveSafePath`/`MimeFor` (Task 3), `ProxyResponse` (Abstractions).

- [ ] **Step 1: Write the failing serving tests**

Append to `MovieLibrarySourceTests`:
```csharp
    private async Task<string> FirstItemIdAsync()
        => (await Source().SearchAsync("movies", "some", Ctx(), default)).Items.Single().Id;

    [Fact]
    public async Task Open_without_a_range_serves_the_whole_file()
    {
        await using var r = await Source().OpenAsync(await FirstItemIdAsync(), null, default);
        Assert.NotNull(r);
        Assert.Equal(200, r!.StatusCode);
        Assert.Equal(4, r.ContentLength);
        Assert.Equal("bytes", r.AcceptRanges);
        using var sink = new MemoryStream();
        await r.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.ToArray());
    }

    [Fact]
    public async Task Open_with_a_range_serves_206_and_the_slice()
    {
        await using var r = await Source().OpenAsync(await FirstItemIdAsync(), "bytes=1-2", default);
        Assert.NotNull(r);
        Assert.Equal(206, r!.StatusCode);
        Assert.Equal(2, r.ContentLength);
        Assert.Equal("bytes 1-2/4", r.ContentRange);
        Assert.Equal("bytes", r.AcceptRanges);
        using var sink = new MemoryStream();
        await r.Body.CopyToAsync(sink);
        Assert.Equal(new byte[] { 2, 3 }, sink.ToArray());
    }

    [Fact]
    public async Task Open_with_an_unsatisfiable_range_is_416()
    {
        await using var r = await Source().OpenAsync(await FirstItemIdAsync(), "bytes=100-200", default);
        Assert.NotNull(r);
        Assert.Equal(416, r!.StatusCode);
        Assert.Equal("bytes */4", r.ContentRange);
    }

    [Fact]
    public async Task Open_on_an_out_of_roots_id_returns_null()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ebs-outside-" + Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllBytes(outside, new byte[] { 9 });
        try
        {
            var evilId = MovieLibrarySource.EncodeId(outside);
            Assert.Null(await new MovieLibrarySource([_root], NullLogger<MovieLibrarySource>.Instance).OpenAsync(evilId, null, default));
        }
        finally { File.Delete(outside); }
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~MovieLibrarySource" -v minimal`
Expected: FAIL — `OpenAsync` currently returns null (the default), so the 200/206/416 assertions fail.

- [ ] **Step 3: Implement `OpenAsync` + expand `MimeFor`**

In `MovieLibrarySource.cs`, add:
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

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true);

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
Await note: the body is `async` per the interface signature but does no `await` — either add `await Task.CompletedTask;` or drop `async` and return `Task.FromResult`. Match the interface's exact return type (`Task<ProxyResponse?>`).

Expand `MimeFor` to a small extension→MIME map: `.mkv`→`video/x-matroska`, `.mp4`/`.m4v`→`video/mp4`, `.avi`→`video/x-msvideo`, `.mov`→`video/quicktime`, `.webm`→`video/webm`, `.wmv`→`video/x-ms-wmv`, `.flv`→`video/x-flv`, `.ts`→`video/mp2t`, `.mpg`/`.mpeg`→`video/mpeg`; default `application/octet-stream`. Case-insensitive on the extension.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test EverythingBox.Server.Tests --filter "FullyQualifiedName~MovieLibrarySource" -v minimal`
Expected: PASS — 200/206/416/out-of-roots all green.

- [ ] **Step 5: Write the README**

Create `EverythingBox.Server.LocalLibrary/README.md` (mirror SampleSource's): what the plugin does (movies for now), the config section (`"Plugins": { "locallib": { "Movies": ["…"] } }`), and the install step — build, then copy ONLY `EverythingBox.Server.LocalLibrary.dll` into the server's `plugins/locallib/` directory, never `EverythingBox.Server.Abstractions.dll`.

- [ ] **Step 6: Full-suite check + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal` then `dotnet test EverythingBox.Server.Core.Tests -v minimal`
Expected: both green, including `RepositoryCleanlinessTests` (the new plugin names nothing denylisted) and the existing `SampleSourceTests`.

```bash
git add EverythingBox.Server.LocalLibrary/MovieLibrarySource.cs EverythingBox.Server.LocalLibrary/README.md EverythingBox.Server.Tests/MovieLibrarySourceTests.cs
git commit -m "feat: serve local movie files with HTTP Range (206/200/416)"
```

---

## Self-review

**Spec coverage:**
- New project mirroring SampleSource (`Private="false"`, tests-only ref, staged into `plugins/locallib/`) → Task 1 Step 1. ✅
- `LocalLibraryConfig { Movies }` + plugin registering `MovieLibrarySource` → Task 3 Step 3. ✅
- Conditional `movies` catalog; scan via `EnumerationOptions`; classify via `DefaultReleaseParser` + `MediaFileMatcher.VideoExtensions`; query filter; `Subtitle`; ordering; cap → Task 3 Step 3. ✅
- Path security (EncodeId/ResolveSafePath, real-resolve + containment, re-validated) mirroring `LocalFolderSource` → Task 3 (before-writing note + Step 3) + adversarial tests (Task 3 Step 1, Task 4 Step 1). ✅
- `RangeRequest.Parse` rules (Full/Partial/Unsatisfiable, all header forms) → Task 1. ✅
- `BoundedReadStream` (bounded slice, owns/disposes inner) → Task 2. ✅
- `OpenAsync` 206/200/416, `Accept-Ranges`, `Content-Range`/`Content-Length`, no FileStream leak → Task 4 Step 3 + disposal via `BoundedReadStream`/`ProxyResponse`. ✅
- `ResolveAsync` proxy URL; `DetailAsync` empty; MIME map → Task 3 + Task 4. ✅
- No host/contract change, no API bump; cleanliness green → Global Constraints; no task touches `ServerApi`/Abstractions. ✅
- Series/NFO/artwork/music out of scope → no task adds them. ✅

**Placeholder scan:** none — every code step shows complete code except the deliberately-delegated path-security replication (Task 3 "read `LocalFolderSource` and replicate exactly"), which is gated by concrete adversarial tests rather than re-derived loosely (safer for security code).

**Type consistency:** `RangeResult`/`RangeKind` produced in Task 1, consumed in Task 4's `OpenAsync`. `BoundedReadStream(Stream, long)` produced in Task 2, constructed in Task 4. `MovieLibrarySource(IReadOnlyList<string>, ILogger)` + `internal static EncodeId` + `ResolveSafePath`/`MimeFor` produced in Task 3, used by Task 4. Key `"locallib"` and media type `"movie"` consistent throughout. Tests reference `MovieLibrarySource.EncodeId` (internal, exposed via the project's `InternalsVisibleTo("EverythingBox.Server.Tests")` from Task 1's csproj).
