# ROM Library — Increment 2 (RomLibrary plugin MVP) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new in-repo plugin `EverythingBox.Server.RomLibrary` (`"romlib"`) that scans configured ROM library roots, advertises a `games` catalog of `platform` containers (one per system folder, titled with a recognizable console name), drills each into its ROM `game` items, and serves them with HTTP Range through the Increment-1 `SafeLocalFileServer`.

**Architecture:** Mirrors the LocalLibrary plugin. One `RomLibrarySource : IMediaSource`. The catalog is two-level like LocalLibrary's series→episodes: a `games` catalog whose items are `type:"platform"` (each an immediate subfolder of a root), expanded by `DetailAsync` into `type:"game"` files. The **platform item title** is the only channel that sets the client's `systemHint`, so it must be a `forConsoleName`-recognizable console name — an embedded `RomSystems` table maps folder names to (systemId, consoleTitle). Serving/containment is the Increment-1 `SafeLocalFileServer`, reused verbatim (one instance — ROMs have a single root class).

**Tech Stack:** .NET 9 / C#, xUnit. New project `EverythingBox.Server.RomLibrary`; tests in `EverythingBox.Server.Tests`.

## Global Constraints

- **Reuse Increment-1 security verbatim.** All id minting, containment, and Range serving go through `EverythingBox.Server.Abstractions.SafeLocalFileServer` (`EncodeId`, `ResolveSafeFile`, `ResolveSafeDir`, `IsContained`, `OpenAsync`). Write NO new path/containment code.
- **The platform item title is contract-critical** — it must be a recognizable console name (the client derives `systemHint`/emulator from it; nothing else carries the console). The `RomSystems` titles are chosen for this.
- **No API bump.** Only existing contract surface is used (`IMediaSource`, `CatalogDescriptor`, `CatalogItem` with `MediaType` "platform"/"game", `SourceCatalog`, `SourceStream`, existing routes). Engine stays at `ServerApi.VersionString` = "1.14". Do not touch `ServerApi.cs` or the version-pin tests.
- **In-repo plugin, engine-invisible.** csproj mirrors LocalLibrary's: `Private="false"` Abstractions ref, `InternalsVisibleTo("EverythingBox.Server.Tests")`, referenced ONLY by the Tests project, NOT by `EverythingBox.Server`. Fresh-checkout-serves-nothing: empty `Catalogs` when no roots configured.
- PUBLIC repo cleanliness — no external content-source name anywhere (code/paths/commit messages). `RepositoryCleanlinessTests` stays green. (ROMs/systems are generic; no source name involved.)
- Stage by explicit path (never `git add -A`); no AI attribution in commits. No new NuGet package. Run tests per project.

---

### Task 1: Scaffold the plugin, the system table, and the platform listing

**Files:**
- Create: `EverythingBox.Server.RomLibrary/EverythingBox.Server.RomLibrary.csproj`
- Create: `EverythingBox.Server.RomLibrary/RomLibraryConfig.cs`
- Create: `EverythingBox.Server.RomLibrary/RomSystems.cs`
- Create: `EverythingBox.Server.RomLibrary/RomLibraryPlugin.cs`
- Create: `EverythingBox.Server.RomLibrary/RomLibrarySource.cs` (Catalogs + SearchAsync now; Detail/Resolve/Open are honest stubs filled in Task 2)
- Modify: `EverythingBoxServer.sln` (add the project)
- Modify: `EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj` (add a `<ProjectReference>` to the new plugin, mirroring the existing LocalLibrary reference)
- Create: `EverythingBox.Server.Tests/RomSystemsTests.cs`
- Create: `EverythingBox.Server.Tests/RomLibrarySourceTests.cs`

**Interfaces:**
- Produces: `RomLibrarySource(IReadOnlyList<string> roots, ILogger logger)` with `Key => "romlib"`, `Catalogs`, `SearchAsync`. `RomSystems.Resolve(string folderName) → (string Id, string Title)?`. `RomLibraryConfig { List<string> Roms }`. `RomLibraryPlugin : IPlugin`.

- [ ] **Step 1: Create the project file** (`EverythingBox.Server.RomLibrary/EverythingBox.Server.RomLibrary.csproj`) — copy LocalLibrary's verbatim:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="EverythingBox.Server.Tests" />
  </ItemGroup>
  <ItemGroup>
    <!-- The host supplies Abstractions at load time; shipping a second copy makes every cast fail. -->
    <ProjectReference Include="..\EverythingBox.Server.Abstractions\EverythingBox.Server.Abstractions.csproj" Private="false" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the project to the solution and reference it from Tests**

Run: `dotnet sln EverythingBoxServer.sln add EverythingBox.Server.RomLibrary/EverythingBox.Server.RomLibrary.csproj`
Then add to `EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj`, in the same `<ItemGroup>` that already `<ProjectReference>`s `EverythingBox.Server.LocalLibrary`, a sibling line:
```xml
    <ProjectReference Include="..\EverythingBox.Server.RomLibrary\EverythingBox.Server.RomLibrary.csproj" />
```
(Read the Tests csproj first; match the exact form of the LocalLibrary reference line, whatever attributes it carries.)

- [ ] **Step 3: `RomLibraryConfig.cs`**

```csharp
namespace EverythingBox.Server.RomLibrary;

public sealed class RomLibraryConfig
{
    /// <summary>Absolute paths to ROM library roots. Each immediate subfolder of a root is a system
    /// (named by the console — snes/, psx/, megadrive/…); the files inside it are that system's games.</summary>
    public List<string> Roms { get; set; } = [];
}
```

- [ ] **Step 4: `RomSystems.cs` — the folder → (systemId, consoleTitle) table**

The one unavoidable duplication (the public repo can't reference the client's `SystemCatalog`). Keys are normalized folder names; titles are full console names chosen to be recognizable to the client's console-name matcher.

```csharp
namespace EverythingBox.Server.RomLibrary;

/// <summary>
/// Maps a ROM folder name to a canonical (systemId, consoleTitle). The <b>title</b> is contract-critical:
/// the EverythingBox client derives which emulator/core to use from the parent platform item's title
/// (its console-name matcher), not from any game field — so each title is a recognizable console name.
/// Data-only, intentionally duplicated from the client's system list because a public plugin cannot
/// reference the client. An unrecognized folder is not an error: the caller lists it under its own name.
/// </summary>
internal static class RomSystems
{
    // Normalize a folder name the way the client's aliases are written: letters/digits only, lowercased.
    // "Mega Drive" -> "megadrive", "Sega-32X" -> "sega32x".
    private static string Norm(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s) if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    // normalized folder alias -> (systemId, consoleTitle)
    private static readonly Dictionary<string, (string Id, string Title)> Map = BuildMap();

    private static Dictionary<string, (string, string)> BuildMap()
    {
        var m = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        void Add(string id, string title, params string[] aliases)
        { foreach (var a in aliases) m[Norm(a)] = (id, title); }

        Add("nes", "Nintendo Entertainment System", "nes", "famicom", "fc");
        Add("snes", "Super Nintendo Entertainment System", "snes", "superfamicom", "sfc", "supernintendo");
        Add("n64", "Nintendo 64", "n64", "nintendo64");
        Add("gb", "Nintendo Game Boy", "gb", "gameboy", "gbc", "gameboycolor");
        Add("gba", "Nintendo Game Boy Advance", "gba", "gameboyadvance");
        Add("nds", "Nintendo DS", "nds", "ds", "nintendods");
        Add("gc", "Nintendo GameCube", "gc", "gamecube", "ngc");
        Add("virtualboy", "Nintendo Virtual Boy", "virtualboy", "vb");
        Add("genesis", "Sega Genesis", "genesis", "megadrive", "md", "segagenesis", "segamegadrive");
        Add("genesis", "Sega Master System", "mastersystem", "sms", "segamastersystem");
        Add("genesis", "Sega Game Gear", "gamegear", "gg", "segagamegear");
        Add("32x", "Sega 32X", "32x", "sega32x");
        Add("segacd", "Sega CD", "segacd", "megacd");
        Add("saturn", "Sega Saturn", "saturn", "segasaturn");
        Add("dreamcast", "Sega Dreamcast", "dreamcast", "dc", "segadreamcast");
        Add("psx", "Sony PlayStation", "psx", "ps1", "playstation", "psone");
        Add("ps2", "Sony PlayStation 2", "ps2", "playstation2");
        Add("psp", "Sony PSP", "psp", "playstationportable");
        Add("pce", "PC Engine", "pce", "pcengine", "tg16", "turbografx16", "turbografx");
        Add("neogeo", "SNK Neo Geo", "neogeo", "neogeoaes", "neogeomvs");
        Add("ws", "WonderSwan", "ws", "wonderswan", "wsc", "wonderswancolor");
        Add("ngp", "Neo Geo Pocket", "ngp", "neogeopocket", "ngpc");
        Add("lynx", "Atari Lynx", "lynx", "atarilynx");
        Add("a2600", "Atari 2600", "a2600", "atari2600");
        Add("a7800", "Atari 7800", "a7800", "atari7800");
        Add("c64", "Commodore 64", "c64", "commodore64");
        Add("amiga", "Commodore Amiga", "amiga", "commodoreamiga");
        Add("msdos", "MS-DOS", "dos", "msdos", "pc");
        return m;
    }

    /// <summary>Resolve a folder name to its canonical (systemId, consoleTitle), or null if unknown.</summary>
    public static (string Id, string Title)? Resolve(string folderName)
        => Map.TryGetValue(Norm(folderName), out var v) ? v : null;
}
```

- [ ] **Step 5: `RomLibraryPlugin.cs`** — mirror `LocalLibraryPlugin`, no cache for MVP:

```csharp
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.RomLibrary;

public sealed class RomLibraryPlugin : IPlugin
{
    public string Key => "romlib";
    public string DisplayName => "ROM Library";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        var config = context.GetConfig<RomLibraryConfig>() ?? new RomLibraryConfig();
        registry.AddSource(new RomLibrarySource(config.Roms, context.Loggers.CreateLogger<RomLibrarySource>()));
    }
}
```

- [ ] **Step 6: `RomLibrarySource.cs` — Catalogs + SearchAsync (platform listing); Detail/Resolve/Open stubs**

```csharp
using EverythingBox.Server.Abstractions;
using Microsoft.Extensions.Logging;

namespace EverythingBox.Server.RomLibrary;

/// <summary>
/// Scans configured ROM roots. Each immediate subfolder of a root is a console: it becomes an
/// expandable "platform" item in a single "games" catalog, titled with a recognizable console name so
/// the client picks the right emulator (the platform title is the ONLY systemHint channel). DetailAsync
/// expands a platform into its ROM files as "game" items; bytes are relayed with HTTP Range through the
/// host proxy route. Every id is decoded, real-resolved and confirmed inside a root before anything is
/// served or expanded — via the shared SafeLocalFileServer; an id is never trusted on its own.
/// </summary>
public sealed class RomLibrarySource : IMediaSource
{
    private const int MaxItems = 5000;

    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger _logger;

    // One server over the ROM roots: ResolveSafeFile/OpenAsync serve a game file, ResolveSafeDir gates a
    // platform folder (a strict subfolder of a root), IsContained backstops enumeration. ROMs have a
    // single root class, so unlike LocalLibrary one instance covers both file and directory resolution.
    private readonly SafeLocalFileServer _files;

    public RomLibrarySource(IReadOnlyList<string> roots, ILogger logger)
    {
        _roots = roots;
        _logger = logger;
        _files = new SafeLocalFileServer(roots, MimeFor);
    }

    public string Key => "romlib";

    // A fresh checkout with no configured roots serves nothing.
    public IReadOnlyList<CatalogDescriptor> Catalogs
        => _roots.Count > 0 ? [new CatalogDescriptor("games", "Games", "game")] : [];

    // Immediate subfolders only; a junction/symlink subfolder is skipped (never descended) so it can't
    // leak a console folder from outside a root. IgnoreInaccessible skips an unreadable dir.
    private static readonly EnumerationOptions TopLevelDirs = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
    {
        if (catalogId != "games") return Task.FromResult(SourceCatalog.Empty("ROM Library"));

        var items = new List<CatalogItem>();
        var capped = false;

        foreach (var root in _roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", TopLevelDirs))
            {
                ct.ThrowIfCancellationRequested();
                if (!_files.IsContained(dir)) continue; // backstop; TopLevelDirs already blocks junctions

                var folderName = Path.GetFileName(dir);
                var title = RomSystems.Resolve(folderName)?.Title ?? folderName;

                if (!string.IsNullOrWhiteSpace(query) &&
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }

                items.Add(new CatalogItem(
                    Id: SafeLocalFileServer.EncodeId(dir),
                    Title: title,
                    Subtitle: string.Empty,
                    MediaType: "platform",
                    ThumbnailUrl: null,
                    Expandable: true));
            }
            if (capped) break;
        }

        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult(new SourceCatalog("Games", ordered, capped));
    }

    // Filled in Task 2.
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("ROM Library"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => _files.OpenAsync(itemId, rangeHeader, ct);

    // ext -> "application/x-<ext>" so the client can recover the ROM extension from the mime when the
    // url path lacks it; the proxy url DOES carry the filename, so this is a belt-and-suspenders map.
    // Art extensions map to image types (unused until Inc 3's boxart).
    private static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "" => "application/octet-stream",
            "jpg" or "jpeg" => "image/jpeg",
            "png" => "image/png",
            "webp" => "image/webp",
            _ => $"application/x-{ext}",
        };
    }
}
```
Note: `OpenAsync` is wired now (it is a pure delegate to the shared server and is trivially correct); `DetailAsync`/`ResolveAsync` are honest stubs completed in Task 2.

- [ ] **Step 7: `RomSystemsTests.cs`**

```csharp
using EverythingBox.Server.RomLibrary;
using Xunit;

namespace EverythingBox.Server.Tests;

public class RomSystemsTests
{
    [Theory]
    [InlineData("snes", "snes")]
    [InlineData("Super Famicom", "snes")]
    [InlineData("megadrive", "genesis")]
    [InlineData("Mega Drive", "genesis")]
    [InlineData("psx", "psx")]
    [InlineData("PlayStation", "psx")]
    [InlineData("tg16", "pce")]
    public void Resolves_known_folders_to_their_system_id(string folder, string expectedId)
        => Assert.Equal(expectedId, RomSystems.Resolve(folder)!.Value.Id);

    [Fact]
    public void Known_folder_has_a_nonempty_console_title()
        => Assert.False(string.IsNullOrWhiteSpace(RomSystems.Resolve("snes")!.Value.Title));

    [Fact]
    public void Unknown_folder_resolves_to_null()
        => Assert.Null(RomSystems.Resolve("totallynotaconsole"));
}
```

- [ ] **Step 8: `RomLibrarySourceTests.cs` (Task-1 slice: Catalogs + platform listing)**

Use a temp-dir fixture (mirror `LocalLibrarySourceTests`' temp-root setup and `NullLogger`). Write:
- `No_roots_configured_has_no_catalogs` → `Assert.Empty(new RomLibrarySource([], NullLogger<RomLibrarySource>.Instance).Catalogs)`.
- `A_configured_root_advertises_the_games_catalog` → single catalog, `Id="games"`, `MediaType="game"`.
- `Each_system_subfolder_becomes_a_platform_item` → a root containing `snes/` and `psx/` subfolders → `SearchAsync("games", null, new SourceContext(), default)` returns two items, both `MediaType="platform"`, `Expandable==true`, titles `RomSystems.Resolve("snes").Title` / `Resolve("psx").Title`, ids that round-trip via `SafeLocalFileServer.TryDecodeId`.
- `An_unrecognized_folder_is_titled_by_its_folder_name` → a `weirdbox/` subfolder → an item with `Title=="weirdbox"`, `MediaType="platform"`.
- `A_non_games_catalog_is_empty` → `SearchAsync("nope", …)` → `Items` empty.

Use `NullLogger<RomLibrarySource>.Instance` (`using Microsoft.Extensions.Logging.Abstractions;`). Create the subfolders with `Directory.CreateDirectory`; a platform needs no files to list. Clean up the temp dir in `Dispose`.

- [ ] **Step 9: Build + run + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal`
Expected: green, including the new `RomSystemsTests` + `RomLibrarySourceTests` (Task-1 slice), and `RepositoryCleanlinessTests` still green.
```bash
git add EverythingBox.Server.RomLibrary/EverythingBox.Server.RomLibrary.csproj EverythingBox.Server.RomLibrary/RomLibraryConfig.cs EverythingBox.Server.RomLibrary/RomSystems.cs EverythingBox.Server.RomLibrary/RomLibraryPlugin.cs EverythingBox.Server.RomLibrary/RomLibrarySource.cs EverythingBoxServer.sln EverythingBox.Server.Tests/EverythingBox.Server.Tests.csproj EverythingBox.Server.Tests/RomSystemsTests.cs EverythingBox.Server.Tests/RomLibrarySourceTests.cs
git commit -m "feat: RomLibrary plugin scaffold — games catalog of platform items per system folder"
```

---

### Task 2: Drill a platform into its ROMs, and resolve the stream

**Files:**
- Modify: `EverythingBox.Server.RomLibrary/RomLibrarySource.cs` (implement `DetailAsync` + `ResolveAsync`; add the junk filter)
- Modify: `EverythingBox.Server.Tests/RomLibrarySourceTests.cs` (add the drill/serve/containment cases)

**Interfaces:**
- Consumes: `RomLibrarySource`, `SafeLocalFileServer` (`ResolveSafeDir`/`ResolveSafeFile`/`OpenAsync`), `RomSystems` from Task 1.
- Produces: `DetailAsync("platform" id) → game items`; `ResolveAsync("game" id) → SourceStream`.

- [ ] **Step 1: Add the junk filter + file enumeration options** to `RomLibrarySource`

```csharp
    private static readonly EnumerationOptions TopLevelFiles = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    // Not-a-game files that commonly sit beside ROMs. A dotfile or one of these extensions is skipped;
    // everything else in a system folder is treated as a playable ROM (the folder is authoritative,
    // matching how the client accepts any non-junk file under a system folder).
    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".nfo", ".xml", ".dat", ".md", ".ini", ".cfg", ".db", ".log",
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
        ".m3u", ".srm", ".state", ".sav", ".bak",
    };

    private static bool IsRom(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name[0] == '.') return false; // dotfiles
        return !JunkExtensions.Contains(Path.GetExtension(path));
    }
```

- [ ] **Step 2: Implement `DetailAsync`** (replace the Task-1 stub)

```csharp
    // A platform id is a system folder (a real directory strictly inside a root, per ResolveSafeDir).
    // Its immediate non-junk files are the games. A game/file id has nothing to expand → empty.
    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeDir(itemId) is not { } systemDir)
            return Task.FromResult(SourceCatalog.Empty("ROM Library"));

        var items = new List<CatalogItem>();

        foreach (var path in Directory.EnumerateFiles(systemDir, "*", TopLevelFiles))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsRom(path)) continue;
            if (!_files.IsContained(path)) continue; // backstop
            if (items.Count >= MaxItems) break;

            items.Add(new CatalogItem(
                Id: SafeLocalFileServer.EncodeId(path),
                Title: Path.GetFileNameWithoutExtension(path),
                Subtitle: Path.GetFileName(path),
                MediaType: "game",
                ThumbnailUrl: null,
                Expandable: false));
        }

        var title = RomSystems.Resolve(Path.GetFileName(systemDir))?.Title ?? Path.GetFileName(systemDir);
        var ordered = items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        return Task.FromResult(new SourceCatalog(title, ordered));
    }
```

- [ ] **Step 3: Implement `ResolveAsync`** (replace the Task-1 stub)

```csharp
    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
    {
        if (_files.ResolveSafeFile(itemId) is not { } path)
            return Task.FromResult<SourceStream?>(null);

        // A relative addon path the host serves from the proxy route (OpenAsync). The filename — with
        // its extension — is in the path, so the client keeps the extension for the emulator.
        var url = $"proxy/{Key}/{itemId}/{Uri.EscapeDataString(Path.GetFileName(path))}";
        return Task.FromResult<SourceStream?>(new SourceStream(url, MimeFor(path)));
    }
```
(Leave `OpenAsync` as the Task-1 delegate.)

- [ ] **Step 4: Add the drill/serve/containment tests** to `RomLibrarySourceTests.cs`

Add cases (temp-dir fixture; write small byte files as ROMs):
- `A_platform_expands_into_its_rom_files` → root with `snes/` containing `Game A.sfc`, `Game B.sfc` and a junk `notes.txt` + a dotfile `.DS_Store`; platform id = `SafeLocalFileServer.EncodeId(<snes dir>)`; `DetailAsync(id, …)` → exactly the two `.sfc` items, `MediaType=="game"`, `Expandable==false`, titles `"Game A"`/`"Game B"` (no extension), sorted; the `.txt` and dotfile absent.
- `Detail_on_a_foreign_directory_id_is_empty` → an id encoding a directory OUTSIDE any root → `DetailAsync` returns empty items.
- `Detail_on_a_game_file_id_is_empty` → a file id (a ROM) passed to `DetailAsync` → empty (a file is not a platform; `ResolveSafeDir`'s `Directory.Exists` gate rejects it).
- `Resolve_on_a_game_id_returns_a_proxy_stream` → `ResolveAsync(<rom id>, 0, …)` → non-null `SourceStream` whose `Url` starts `proxy/romlib/` and ends with the escaped filename, and `Mime == "application/x-sfc"`.
- `Resolve_on_a_foreign_id_is_null` → an id encoding a path outside the roots → `ResolveAsync` null.
- `Open_serves_the_rom_with_range` → `OpenAsync(<rom id>, "bytes=0-3", default)` → non-null `ProxyResponse`, `StatusCode==206`, `Body` yields the first 4 bytes; and `OpenAsync(<rom id>, null, default)` → `StatusCode==200` with the full bytes. (One smoke test each — the Range engine itself is covered by `SafeLocalFileServerTests`.)
- `Open_on_a_traversal_id_serves_nothing` → an id encoding a path outside the roots → `OpenAsync` null.

(For the "foreign directory/path" ids, encode an absolute temp path that is NOT under any configured root via `SafeLocalFileServer.EncodeId`, mirroring how `LocalLibrarySourceTests` builds its `evilId`.)

- [ ] **Step 5: Build + run + commit**

Run: `dotnet test EverythingBox.Server.Tests -v minimal`
Expected: green — the full `RomLibrarySourceTests` (Task 1 + Task 2 cases), `RomSystemsTests`, `RepositoryCleanlinessTests`.
Also run `dotnet test EverythingBox.Server.Core.Tests -v minimal` once to confirm nothing engine-side regressed (it should not — no engine file changed).
```bash
git add EverythingBox.Server.RomLibrary/RomLibrarySource.cs EverythingBox.Server.Tests/RomLibrarySourceTests.cs
git commit -m "feat: RomLibrary — expand a platform into its ROMs and resolve the game stream"
```

---

## Self-review

**Spec coverage:** `games` catalog of `platform` items titled by a recognizable console name (spec Architecture) → Task 1 Steps 4,6,8. Drill platform→`game` files, junk-filtered, stem titles (spec) → Task 2 Steps 1,2,4. `/stream` proxy url + `application/x-<ext>` mime (spec) → Task 2 Step 3. Range serving via the Inc-1 server (spec) → Task 1 Step 6 `OpenAsync` delegate + Task 2 Step 4 serve test. Config `Roms` roots, fresh-checkout-empty (spec) → Task 1 Steps 3,6,8. Embedded `RomSystems` table, unknown→folder-name (spec) → Task 1 Steps 4,7,8. No API bump / in-repo / cleanliness (spec) → Global Constraints, no `ServerApi`/engine edit. Path-traversal tests (spec Testing) → Task 2 Step 4. ✅

**Placeholder scan:** none — every step carries complete code or a fully enumerated test list with concrete assertions. The Task-1 Detail/Resolve stubs are explicitly honest placeholders completed in Task 2 (the tree compiles and Task-1 tests pass without exercising them).

**Type consistency:** `RomLibrarySource(IReadOnlyList<string>, ILogger)`, `Key="romlib"`, `Catalogs`, `SearchAsync`/`DetailAsync`/`ResolveAsync`/`OpenAsync` match `IMediaSource` (verified signatures: `SearchAsync(string,string?,SourceContext,CancellationToken)`, `DetailAsync(string,SourceContext,CancellationToken)`, `ResolveAsync(string,int,SourceContext,CancellationToken)`, `OpenAsync(string,string?,CancellationToken)`). `CatalogItem(Id,Title,Subtitle,MediaType,ThumbnailUrl?,Expandable)`, `CatalogDescriptor(Id,Name,MediaType)`, `SourceCatalog(Title,Items,HasMore)`/`.Empty`, `SourceStream(Url,Mime)`, `SourceContext()` — all match `Catalog.cs`. `RomSystems.Resolve → (string Id,string Title)?`. `RomLibraryPlugin` mirrors `LocalLibraryPlugin` (`GetConfig`/`Loggers.CreateLogger`/`registry.AddSource`). ✅
