# Explicit-member selection on the self-download path (EBS#19)

**Status:** approved 2026-08-10, ready for planning.

## Goal

Let a caller specify **exactly which member files of a torrent to fetch**, instead of relying
only on the request-driven heuristic. Carry the selection on `TorrentResult`; honor it in the
self-download path; leave every existing path unchanged when it is absent. Generic engine
capability — names no content source.

## Why this is needed

`MonoTorrentDownloader.SelectWantedFiles` (`EverythingBox.Server/Download/MonoTorrentDownloader.cs:198`)
picks which members of a torrent to download in exactly one way:

```csharp
private static IReadOnlyList<ITorrentManagerFile> SelectWantedFiles(TorrentManager manager, MediaRequest? request)
{
    var files = manager.Files.ToList();
    return request is null
        ? files
        : MediaFileMatcher.Select(request, files, f => f.Path, f => (long?)f.Length);
}
```

`MediaFileMatcher.Select` is a fuzzy, request-driven match tuned for episode / track / album /
pack shapes. For a very large multi-file torrent (thousands of members) where the caller
**already knows** the exact member(s) it wants, that heuristic is fragile — near-duplicate
names, sequels, and regional variants all defeat "fewest extra title tokens." There is no way
today to say "from this torrent, fetch exactly these members." This adds that.

## The channel: `TorrentResult.WantedMembers`

The member list must travel from the component that **knows** which members it wants (a source
that resolves an item to a specific torrent) to the host downloader. That component mints the
`TorrentResult`; the downloader already receives the `TorrentResult`. So the selection rides on
`TorrentResult` — **no `ITorrentDownloader.DownloadAsync` signature change** is needed (a new
`DownloadAsync` parameter would have nothing source-aware to fill it, since the resolver builds
the `MediaRequest` from the media request, not from member paths).

Add to `EverythingBox.Server.Abstractions/Results/TorrentResult.cs`:

```csharp
/// <summary>
/// Explicit torrent member(s) to fetch — each entry a full in-torrent member path or a bare
/// filename. When non-empty, the self-download path fetches exactly the matching members and
/// ignores the request heuristic. Empty (the default) keeps request-driven matching. Only the
/// self-download (MonoTorrent) path consults this; debrid/direct paths are unaffected.
/// </summary>
public IReadOnlyList<string> WantedMembers { get; init; } = [];
```

Additive, default empty → no behavior change for any existing producer. **API bump 1.10 → 1.11.**

## Honoring it: selection

Change `SelectWantedFiles` to consult `torrent.WantedMembers` first. The downloader already has
the `torrent` in scope at the call site (`MonoTorrentDownloader.cs:86`), so pass the wanted-member
list in:

```csharp
private static IReadOnlyList<ITorrentManagerFile> SelectWantedFiles(
    TorrentManager manager, MediaRequest? request, IReadOnlyList<string> wantedMembers)
{
    var files = manager.Files.ToList();

    if (wantedMembers.Count > 0)
        return SelectMembers(files, f => f.Path, wantedMembers); // explicit selection wins

    return request is null
        ? files
        : MediaFileMatcher.Select(request, files, f => f.Path, f => (long?)f.Length);
}
```

### The pure, testable core: `SelectMembers`

The live `TorrentManager`/`ITorrentManagerFile` path cannot be unit-tested without a swarm, so
the matching logic is extracted into a pure generic helper (the same idiom as `ExceedsCap` from
EBS#5) that operates on any file-like `T` via a path accessor:

```csharp
/// <summary>
/// The subset of <paramref name="files"/> whose full member path OR filename (last path
/// segment) equals one of <paramref name="wantedMembers"/>, compared case-insensitively.
/// Order follows <paramref name="files"/>. A member that matches nothing contributes nothing —
/// so an all-miss selection yields an empty list (the caller then downloads nothing, never the
/// whole torrent).
/// </summary>
internal static IReadOnlyList<T> SelectMembers<T>(
    IReadOnlyList<T> files, Func<T, string> pathOf, IReadOnlyList<string> wantedMembers)
{
    var wanted = new HashSet<string>(wantedMembers, StringComparer.OrdinalIgnoreCase);
    return files.Where(f =>
    {
        var path = pathOf(f);
        return wanted.Contains(path) || wanted.Contains(FileName(path));
    }).ToList();
}

// Last path segment, tolerant of both separators (torrent member paths use '/').
private static string FileName(string path)
{
    var slash = path.LastIndexOfAny(['/', '\\']);
    return slash >= 0 ? path[(slash + 1)..] : path;
}
```

### Matching semantics (approved)

A torrent file matches a `WantedMembers` entry when its **full in-torrent path equals the entry,
OR its filename (last `/`- or `\`-separated segment) equals the entry** — compared
**case-insensitively** (`OrdinalIgnoreCase`). This lets a caller supply either a precise path or
just a bare filename (e.g. `"Super Mario Bros. (World).nes"`) without knowing the torrent's
directory layout, and tolerates case differences. Substring/glob matching was rejected as too
easy to over-match on a large torrent.

## No-match is honest

If `WantedMembers` is non-empty but nothing matches, `SelectMembers` returns empty →
`wanted.Count == 0` at `MonoTorrentDownloader.cs:87` → the existing "No files … matched;
nothing to download" path returns `[]`. The download **never** silently falls back to fetching
the whole (possibly enormous) torrent when the explicit selection matched nothing.

## Composition with the size cap (EBS#5)

Selection runs before the size-cap re-check. The explicit members' real summed size
(`wanted.Sum(f => f.Length)`) is still checked against `maxTotalBytes` before `StartAsync`
(`MonoTorrentDownloader.cs:94-105`), so an explicit selection that is nonetheless too large is
still refused. The two features compose with no special-casing.

## Data flow

1. A producer sets `WantedMembers` on the `TorrentResult` it mints (out of scope here — see below).
2. `ReleaseStreamResolver` hands that `TorrentResult` to `MonoTorrentDownloader.DownloadAsync`
   exactly as today (no call-site change — the field rides on `torrent`).
3. After metadata, `SelectWantedFiles` sees `torrent.WantedMembers` is non-empty and selects the
   matching members via `SelectMembers`, bypassing `MediaFileMatcher`.
4. Size cap re-check, deselect-unwanted, download, publish — all unchanged.

## Testing

- **`SelectMembers` unit tests** (in `MonoTorrentDownloaderTests`, targeting the internal helper —
  `EverythingBox.Server` has `InternalsVisibleTo("EverythingBox.Server.Tests")`):
  - exact full-path match selects that member;
  - bare-filename match selects the member whose last segment equals the entry (member is nested
    in a directory);
  - case-insensitive match (entry differs in case from the member path);
  - multiple entries select multiple members, in file order;
  - an entry that matches nothing contributes nothing; an all-miss list yields an empty result;
  - an empty `wantedMembers` list yields an empty result (so `SelectWantedFiles` correctly only
    calls it under the `Count > 0` guard).
- **`SelectWantedFiles` routing** is covered indirectly: with a non-empty `WantedMembers` the
  explicit path is taken; with an empty one the request/`MediaFileMatcher` path is unchanged.
  (The live-manager selection itself stays as-is and is not unit-tested, matching the existing
  design where the swarm-facing parts are thin.)
- **Contract/version:** `TorrentResult` gains `WantedMembers` (default `[]`); `ServerApi`
  `VersionString` 1.10 → 1.11; the two version-pin tests (`ServerApiContractTests`,
  `MetadataContractTests`) update to Minor 11, and `[InlineData(1, 10)]` is added to the
  earlier-minor compat theory.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- Additive contract change only: a new `TorrentResult` field defaulting to `[]`, and a new
  internal selection branch that runs only when the field is non-empty → **no behavior change**
  for any existing producer or path. Single API minor bump 1.10 → 1.11.
- **Cleanliness:** the change is fully generic — no content-source name in code, comments,
  paths, tests, or the commit message. `RepositoryCleanlinessTests` must stay green.
- `ITorrentDownloader.DownloadAsync` signature is **unchanged** (the field rides on `TorrentResult`).
- `MediaFileMatcher` is untouched; the explicit path bypasses it rather than modifying it.

## Out of scope

- Any producer that **sets** `WantedMembers` (a source that resolves an item to specific members
  of a large multi-file torrent). That is downstream, plugin-side work with its own issue; this
  change is only the generic engine capability plus the contract field.
- Verification of the fetched members (EBS#20) and resume/concurrency (EBS#21) — separate issues.
- Changing `MediaFileMatcher`'s heuristics.

## Done when

- A `TorrentResult` carrying `WantedMembers` causes the self-download path to fetch exactly the
  matching members (full-path or filename, case-insensitive), bypassing the request heuristic;
  an all-miss selection downloads nothing rather than the whole torrent; an empty `WantedMembers`
  reproduces today's behavior.
- `SelectMembers` is unit-tested; the size cap still applies; `DownloadAsync` signature unchanged.
- API is 1.11; both engine test projects green including `RepositoryCleanlinessTests` and the
  version-pin/compat tests. Verified in Release.
