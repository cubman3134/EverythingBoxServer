# Multi-title search (EBS#2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let one `MediaRequest` carry `AlternateTitles`; the relevance gate accepts a match against the primary OR any alternate; `TorrentGrabber` fans out over titles × providers (bounded) and the existing dedupe merges — one call, one ranked list.

**Architecture:** Additive, public-repo only. `MediaRequest` (abstract class) gains `AlternateTitles` and a polymorphic `WithTitle` (per-type clone, because Core can't switch on the host-internal request types). `DefaultTorrentRanker.IsRelevant` accepts any candidate title. `TorrentGrabber` queries each (capable provider × distinct title) via `WithTitle`-cloned requests, bounded by a new `MaxConcurrentSearches`; merge+rank use the ORIGINAL request so the gate sees all titles.

**Tech Stack:** .NET 9 / C#, xUnit. `EverythingBox.Server.Abstractions`, `EverythingBox.Server.Core`, host `EverythingBox.Server`; tests in `EverythingBox.Server.Core.Tests`.

## Global Constraints

- **Additive + behaviour-preserving.** Empty `AlternateTitles` ⇒ exactly one search title ⇒ identical to today. Nothing that reads `Title` changes. The primary-title query uses the ORIGINAL `request` object (not a clone).
- **The gate is the correctness point.** `IsRelevant` must accept the primary OR any alternate, and must NOT loosen the season/episode/year/media-type checks.
- **Tie-break = best release wins.** No title-provenance; `DefaultTorrentRanker.Score` is untouched.
- **API bump 1.15 → 1.16** (new `AlternateTitles` + abstract `WithTitle` on the public `MediaRequest`; `MaxConcurrentSearches` on `GrabberOptions`). The private plugin does not subclass `MediaRequest` — no plugin change.
- PUBLIC repo cleanliness — no external content-source name (code/paths/commit messages); `RepositoryCleanlinessTests` green. No new NuGet package. Stage by explicit path; no `git add -A`; no AI attribution.

---

### Task 1: `MediaRequest.AlternateTitles` + `WithTitle` on every subclass + API 1.16

**Files:**
- Modify: `EverythingBox.Server.Abstractions/Requests/MediaRequest.cs`
- Modify: each subclass in `EverythingBox.Server.Abstractions/Requests/` — `MovieRequest`, `TvRequest`, `MusicRequest`, `BookRequest`, `ComicRequest`, `AudiobookRequest`, `PcGameRequest`, `GeneralRequest`
- Modify: `EverythingBox.Server/Sources/ReleaseStreamResolver.cs` (the host-internal `UnknownMediaTypeRequest`)
- Modify: `EverythingBox.Server.Abstractions/ServerApi.cs` (1.15 → 1.16)
- Modify: `EverythingBox.Server.Core.Tests/ServerApiContractTests.cs` + `MetadataContractTests.cs`
- Create: `EverythingBox.Server.Core.Tests/MediaRequestWithTitleTests.cs`

**Interfaces:**
- Produces: `MediaRequest.AlternateTitles` (`IReadOnlyList<string>`, default `[]`); `abstract MediaRequest WithTitle(string title)` implemented by all subclasses.

- [ ] **Step 1: Add `AlternateTitles` + the abstract `WithTitle`** to `MediaRequest` (after `AdditionalTerms`):
```csharp
    /// <summary>Additional names the same work is indexed under (regional/localised/alternate
    /// titles). The primary <see cref="Title"/> stays the display name; these only widen what
    /// counts as a relevance hit and add search terms in the grabber's fan-out.</summary>
    public IReadOnlyList<string> AlternateTitles { get; init; } = [];

    /// <summary>Return a copy of this request with a different primary <see cref="Title"/> and all
    /// other fields preserved. Used by the grabber to query a provider under an alternate title
    /// without the provider changing. Polymorphic because each subtype carries its own fields.</summary>
    public abstract MediaRequest WithTitle(string title);
```

- [ ] **Step 2: Implement `WithTitle` in every subclass** (copy ALL fields; only `Title` differs). Add each override:

`MovieRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new MovieRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles, Edition = Edition };
```
`TvRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new TvRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Season = Season, Episode = Episode, AbsoluteEpisode = AbsoluteEpisode, FullSeason = FullSeason };
```
`MusicRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new MusicRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Artist = Artist, Album = Album, Track = Track };
```
`BookRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new BookRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Author = Author, Format = Format };
```
`ComicRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new ComicRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Author = Author, Volume = Volume, Issue = Issue, Chapter = Chapter, Format = Format };
```
`AudiobookRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new AudiobookRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Author = Author, Narrator = Narrator };
```
`PcGameRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new PcGameRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles };
```
`GeneralRequest`:
```csharp
    public override MediaRequest WithTitle(string title) => new GeneralRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles,
      Kind = Kind, FileType = FileType, FileTypes = FileTypes, FileFilters = FileFilters };
```

- [ ] **Step 3: Implement `WithTitle` on the host-internal `UnknownMediaTypeRequest`** in `ReleaseStreamResolver.cs`:
```csharp
    internal sealed class UnknownMediaTypeRequest : MediaRequest
    {
        public override MediaType MediaType => MediaType.Other;
        public override MediaRequest WithTitle(string title) => new UnknownMediaTypeRequest
        { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles };
    }
```

- [ ] **Step 4: Bump the API version** — `ServerApi.VersionString` "1.15" → "1.16". Update the two version-pin tests to Minor 16 (rename the methods to say 1_16), and add `[InlineData(1, 15)]` to the backward-compat theory.

- [ ] **Step 5: `MediaRequestWithTitleTests`** — for a representative sample (`MovieRequest` with `Edition`+`Year`, `TvRequest` with season/episode, `MusicRequest` with Album, `GeneralRequest` with `Kind`+`FileTypes`, plus one with `AlternateTitles` set): `WithTitle("X")` returns the same concrete type, `Title == "X"`, and every other field (incl. `Year`, `ExternalIds`, `AdditionalTerms`, `AlternateTitles`, and the subtype fields) equals the original's.

- [ ] **Step 6: Build + test + commit**
Run: `dotnet build EverythingBoxServer.sln -c Debug -clp:ErrorsOnly` (must compile — every subclass now implements the abstract member), then `dotnet test EverythingBox.Server.Core.Tests -v minimal`.
```bash
git add EverythingBox.Server.Abstractions/Requests/MediaRequest.cs EverythingBox.Server.Abstractions/Requests/MovieRequest.cs EverythingBox.Server.Abstractions/Requests/TvRequest.cs EverythingBox.Server.Abstractions/Requests/MusicRequest.cs EverythingBox.Server.Abstractions/Requests/BookRequest.cs EverythingBox.Server.Abstractions/Requests/ComicRequest.cs EverythingBox.Server.Abstractions/Requests/AudiobookRequest.cs EverythingBox.Server.Abstractions/Requests/PcGameRequest.cs EverythingBox.Server.Abstractions/Requests/GeneralRequest.cs EverythingBox.Server/Sources/ReleaseStreamResolver.cs EverythingBox.Server.Abstractions/ServerApi.cs EverythingBox.Server.Core.Tests/ServerApiContractTests.cs EverythingBox.Server.Core.Tests/MetadataContractTests.cs EverythingBox.Server.Core.Tests/MediaRequestWithTitleTests.cs
git commit -m "feat: MediaRequest carries AlternateTitles and a polymorphic WithTitle (API 1.16)"
```

---

### Task 2: The relevance gate accepts any title

**Files:**
- Modify: `EverythingBox.Server.Core/Ranking/DefaultTorrentRanker.cs` (`IsRelevant`)
- Modify: `EverythingBox.Server.Core.Tests/TorrentRankerTests.cs`

**Interfaces:**
- Consumes: `MediaRequest.AlternateTitles` (Task 1). No new public surface.

- [ ] **Step 1: Rewrite the title-match head of `IsRelevant`** — replace the single-`subject` derivation (the first block, up to and including the `missing.Count > 0` return) with a candidate-set match. The rest of the method (`MediaTypeMatches`, the `TvRequest` season/episode block, the `MovieRequest` year block) is UNCHANGED.
```csharp
    private static bool IsRelevant(MediaRequest request, TorrentResult r, out string why)
    {
        // Title overlap: every significant token of a requested title should appear in the release
        // title. A request may carry alternate names (regional/localised); a match against the primary
        // OR any alternate counts. For music the primary subject is the album/artist (the pack that's
        // actually on the indexer), not the individual song.
        var primary = request is MusicRequest music ? (music.Album ?? music.Title) : request.Title;
        var candidates = new List<string> { primary };
        candidates.AddRange(request.AlternateTitles);

        var have = Tokenize(r.Title);
        List<string>? primaryMissing = null;
        var matched = false;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var missing = Tokenize(candidate).Where(t => !have.Contains(t)).ToList();
            if (missing.Count == 0) { matched = true; break; }
            primaryMissing ??= missing; // report the primary subject's gap when nothing matches
        }
        if (!matched)
        {
            why = $"title missing terms: {string.Join(", ", primaryMissing ?? [])}";
            return false;
        }

        if (!MediaTypeMatches(request.MediaType, r))
        // ... UNCHANGED from here down (MediaTypeMatches, TvRequest season/episode, MovieRequest year) ...
```
(Keep the exact code below the shown cut identical to the current file.)

- [ ] **Step 2: Add tests to `TorrentRankerTests`** (read the file's existing helpers for building a `MediaRequest` + `TorrentResult` and calling `Rank`):
  - a release whose title matches ONLY an alternate (`AlternateTitles = ["Localised Name"]`, release titled "Localised Name 1080p") is ELIGIBLE (passes the gate);
  - a release matching NEITHER the primary nor any alternate is rejected with "title missing terms";
  - the primary still matches when alternates are present but don't match;
  - music: `MusicRequest { Title = "song", Album = "The Album", AlternateTitles = ["Alt Album"] }` accepts a release titled "Alt Album FLAC";
  - alternates do NOT loosen the other checks: a `TvRequest` with `Season = 2` + a matching alternate title, against a release parsed as season 1, is still rejected (season mismatch); a `MovieRequest` with `Year = 1999` + matching alternate against a release parsed as 2001 is still rejected (year mismatch).

- [ ] **Step 3: Test + commit**
Run: `dotnet test EverythingBox.Server.Core.Tests -v minimal` (green — new acceptance + the unchanged rejection paths).
```bash
git add EverythingBox.Server.Core/Ranking/DefaultTorrentRanker.cs EverythingBox.Server.Core.Tests/TorrentRankerTests.cs
git commit -m "feat: relevance gate accepts a match against any of the request's titles"
```

---

### Task 3: Bounded title × provider fan-out in `TorrentGrabber`

**Files:**
- Modify: `EverythingBox.Server.Core/GrabberOptions.cs` (add `MaxConcurrentSearches`)
- Modify: `EverythingBox.Server.Core/TorrentGrabber.cs` (`SearchInternalAsync`, `QuickGrabAsync`, helpers)
- Modify: `EverythingBox.Server.Core.Tests/GrabberTests.cs` (+ `RankedSearchTests.cs` if that is where multi-provider fakes live)

**Interfaces:**
- Consumes: `MediaRequest.WithTitle`/`AlternateTitles` (Task 1), the accepting gate (Task 2).
- Produces: `GrabberOptions.MaxConcurrentSearches` (int, default 8).

- [ ] **Step 1: Add the option** to `GrabberOptions`:
```csharp
    /// <summary>Maximum provider queries in flight at once across the whole title × provider
    /// fan-out. With no alternate titles this only bounds provider concurrency; a request with
    /// several titles can otherwise launch titles × providers queries at once. Default 8.</summary>
    public int MaxConcurrentSearches { get; init; } = 8;
```

- [ ] **Step 2: Add fan-out helpers** to `TorrentGrabber` (private):
```csharp
    // The distinct search titles for a request: the primary first (queried via the ORIGINAL request
    // so the single-title case is byte-identical to before), then each alternate that is non-blank and
    // not a case-insensitive duplicate of the primary or an earlier alternate.
    private static IEnumerable<MediaRequest> RequestsPerTitle(MediaRequest request)
    {
        yield return request;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { request.Title.Trim() };
        foreach (var alt in request.AlternateTitles)
        {
            if (string.IsNullOrWhiteSpace(alt)) continue;
            if (!seen.Add(alt.Trim())) continue;
            yield return request.WithTitle(alt);
        }
    }

    // Acquire a concurrency slot before querying; a cancellation while WAITING for the slot is
    // reported as a stopped query (matching QueryProviderAsync), not thrown, unless the CALLER cancelled.
    private async Task<QueryResult> BoundedQueryAsync(
        SemaphoreSlim gate, ITorrentProvider provider, MediaRequest request,
        CancellationToken token, CancellationToken callerToken)
    {
        try { await gate.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { return new QueryResult(SafeProviderName(provider), [], "stopped before completing", TimeSpan.Zero); }

        try { return await QueryProviderAsync(provider, request, token, callerToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
```

- [ ] **Step 3: Fan out in `SearchInternalAsync`** — replace the two query-building sites. After computing `capable`, build the (provider × per-title request) pairs and bound them:
```csharp
        var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSearches));
        var pairs = capable.SelectMany(p => RequestsPerTitle(request).Select(req => (provider: p, request: req))).ToList();

        List<QueryResult> queryResults;
        if (_options.QueryProvidersInParallel)
        {
            queryResults = [.. await Task.WhenAll(
                pairs.Select(x => BoundedQueryAsync(gate, x.provider, x.request, token, cancellationToken))).ConfigureAwait(false)];
        }
        else
        {
            queryResults = [];
            foreach (var x in pairs)
                queryResults.Add(await QueryProviderAsync(x.provider, x.request, token, cancellationToken).ConfigureAwait(false));
        }
```
(The sequential path is already concurrency-1, so it needs no gate.) The `Prepare`/`Rank` downstream still receives the ORIGINAL `request` (unchanged), so the gate sees all titles.

- [ ] **Step 4: Fan out in `QuickGrabAsync`** — change the `tasks` construction to the same bounded pairs, best-provider-first with the primary title first per provider (which `RequestsPerTitle` already yields):
```csharp
        var gate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSearches));
        var pairs = capable.SelectMany(p => RequestsPerTitle(request).Select(req => (provider: p, request: req))).ToList();
        var tasks = pairs.Select(x => BoundedQueryAsync(gate, x.provider, x.request, cts.Token, cancellationToken)).ToList();
```
Everything else in `QuickGrabAsync` (the `WhenEach` loop, re-rank on each completion, `cts.Cancel()` on a clearing pick, `WhenAllSafe(tasks)` unwind) is UNCHANGED — the cancel now stops all remaining title × provider queries. `RecordOutcomes(completed, …)` and `Prepare(request, results)` still use the original `request`.

- [ ] **Step 5: Tests** in `GrabberTests` (reuse the file's fake `ITorrentProvider` and `GrabberOptions` builders):
  - **Regression:** a request with empty `AlternateTitles` against N (≤8) fake providers produces exactly the same query count and results as before (one query per provider).
  - **Merge:** a request `{ Title = "A", AlternateTitles = ["B"] }` against a fake provider that returns a distinct release when its query contains "A" vs "B" yields a ranked list containing BOTH releases (proving both titles were queried and the gate accepted the B-only hit).
  - **Dedup across titles:** a fake provider that returns the SAME release (same info hash) for both "A" and "B" yields ONE result after dedupe.
  - **Concurrency cap:** with `MaxConcurrentSearches = 2`, a fake provider that records the max simultaneous in-flight queries (increment on entry, delay, decrement) across a 3-title × 3-provider fan-out never observes more than 2 concurrent.
  - **Quick-grab cuts the whole fan-out:** with `QuickGrabScore` set and a first (provider,title) hit that clears it, remaining title × provider queries are cancelled (assert not all pairs were fully queried — e.g. a counting provider shows fewer than titles × providers completed queries, or the fast path returns before a slow provider's later-title query runs).
  - Use small deterministic fakes; no real network, no process.

- [ ] **Step 6: Full suites + commit**
Run: `dotnet test EverythingBox.Server.Core.Tests -v minimal` then `dotnet test EverythingBox.Server.Tests -v minimal` — both green (incl. `RepositoryCleanlinessTests` and the unchanged host suites).
```bash
git add EverythingBox.Server.Core/GrabberOptions.cs EverythingBox.Server.Core/TorrentGrabber.cs EverythingBox.Server.Core.Tests/GrabberTests.cs
git commit -m "feat: TorrentGrabber fans out over titles x providers, bounded by MaxConcurrentSearches"
```
(Add `RankedSearchTests.cs` to the `git add` list if you put any fan-out test there.)

---

## Self-review

**Spec coverage:** `AlternateTitles` + `WithTitle` (spec §1) → Task 1. Gate accepts any title (spec §2) → Task 2. Bounded titles × providers fan-out, quick-grab still cuts all, dedupe merges (spec §3) → Task 3. Tie-break = best-release-wins, `Score` untouched (spec) → no `Score` change anywhere. API 1.16 (spec) → Task 1 Step 4. Caller-only, no native-multi-term flag, no metadata change (spec Out of scope) → nothing added there. ✅

**Placeholder scan:** none — every `WithTitle` override is spelled out with its exact field set; the `IsRelevant` change shows the replaced head and states the tail is unchanged; the two grabber fan-out sites carry full replacement code; each test lists concrete assertions.

**Type consistency:** `MediaRequest.WithTitle(string) : MediaRequest` (Task 1) consumed by `RequestsPerTitle` (Task 3). `AlternateTitles : IReadOnlyList<string>` read in `IsRelevant` (Task 2) and `RequestsPerTitle` (Task 3). `GrabberOptions.MaxConcurrentSearches : int` (Task 3 Step 1) used in Steps 3–4. `QueryProviderAsync`/`QueryResult`/`SafeProviderName`/`Prepare`/`Prioritize` are existing members reused unchanged. `ServerApi.VersionString = "1.16"`, version tests Minor 16 + `[InlineData(1,15)]`. ✅
