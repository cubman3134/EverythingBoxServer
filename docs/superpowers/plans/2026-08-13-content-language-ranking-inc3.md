# Preferred content language — Increment 3 (torrent ranking) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Torrent movie/TV search ranks a release in the caller's preferred language (audio + subtitles) higher, driven by the `Accept-Language` header the client already sends.

**Architecture:** A per-request language rides a new `MediaRequest.PreferredLanguage` (a release-language NAME). `DefaultTorrentRanker` folds it ahead of the configured `Ranking.PreferredLanguages`/`PreferredSubtitleLanguages` when scoring — additive, no signature changes to the grabber/ranker interfaces. The generic sources (`IndexerSearchSource`, `MetadataBackedVideoSource`) read `Accept-Language` from `SourceContext.RequestHeaders`, map the 2-letter code to the parser's language name, and stamp it on the request.

**Tech Stack:** C# / .NET 9, xUnit. Engine only: `EverythingBox.Server.Abstractions` + `.Core` + `.Server` (all public repo). BCL-only in Core.

## Global Constraints

- **Public-repo clean:** everything is generic language handling (`Accept-Language`, `PreferredLanguage`, English language names). Name NO plugin, NO content source. `RepositoryCleanlinessTests` must stay green.
- **Core stays BCL-only** (`CoreDependencyTests`): no `PackageReference`; only `System.*`/`netstandard`/Abstractions/`Logging.Abstractions`. A hand-rolled `Dictionary` mapper satisfies this.
- **Soft, additive preference:** the per-request language is *prepended* to the configured list; with no header the request field is null and ranking is byte-for-byte today's. An untagged release still scores 0 (no penalty) — only a *tagged, non-preferred* language keeps the existing −10 (video) treatment, exactly as configured `PreferredLanguages` already behaves.
- **Language NAMES, not codes.** The ranker compares against `DefaultReleaseParser`'s output names (`"English"`, `"Spanish"`, …). `MediaRequest.PreferredLanguage` and the mapper output MUST be those names.
- **No AI attribution.** Stage by explicit path; never `git add -A`. EBS default branch `main`.

---

### Task 1: `MediaRequest.PreferredLanguage` + `ContentLanguage` mapper (Abstractions)

**Files:**
- Modify: `EverythingBox.Server.Abstractions/Requests/MediaRequest.cs` (new `PreferredLanguage` init property)
- Modify: `EverythingBox.Server.Abstractions/Requests/{Movie,Tv,Music,Audiobook,Book,Comic,General,PcGame}Request.cs` (copy the field in each `WithTitle`)
- Create: `EverythingBox.Server.Abstractions/ContentLanguage.cs`
- Modify: `EverythingBox.Server.Core.Tests/MediaRequestWithTitleTests.cs` (guard the new field survives `WithTitle`)
- Create: `EverythingBox.Server.Core.Tests/ContentLanguageTests.cs`

**Interfaces:**
- Produces: `string? MediaRequest.PreferredLanguage { get; init; }` (a release-language NAME, e.g. `"Spanish"`; null = none); `static string? ContentLanguage.FromHeaders(IReadOnlyDictionary<string,string>? headers)`.

- [ ] **Step 1: Add the field** to `EverythingBox.Server.Abstractions/Requests/MediaRequest.cs` (after `AlternateTitles`, before `WithTitle`):

```csharp
    /// <summary>A caller's per-request preferred content language, as a release-language NAME
    /// (e.g. "Spanish") — folded ahead of the configured Ranking languages when scoring a
    /// release's audio and subtitle languages. Null = no per-request preference (config only).
    /// Produced from an Accept-Language header via <see cref="ContentLanguage"/>.</summary>
    public string? PreferredLanguage { get; init; }
```

- [ ] **Step 2: Copy it in every `WithTitle`.** In each of the 8 request subtypes, add `PreferredLanguage = PreferredLanguage,` to the object initializer in `WithTitle`. Example — `MovieRequest.cs`:

```csharp
    public override MediaRequest WithTitle(string title) => new MovieRequest
    { Title = title, Year = Year, ExternalIds = ExternalIds, AdditionalTerms = AdditionalTerms, AlternateTitles = AlternateTitles, PreferredLanguage = PreferredLanguage, Edition = Edition };
```
Do the identical one-field addition in `TvRequest.cs`, `MusicRequest.cs`, `AudiobookRequest.cs`, `BookRequest.cs`, `ComicRequest.cs`, `GeneralRequest.cs`, `PcGameRequest.cs` (each has its own `WithTitle` returning its own type — add `PreferredLanguage = PreferredLanguage,` to each initializer). Grep `WithTitle` across `Requests/` to confirm all 8 are updated.

- [ ] **Step 3: Create the mapper** `EverythingBox.Server.Abstractions/ContentLanguage.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace EverythingBox.Server.Abstractions;

/// <summary>Turns a caller's Accept-Language header into the release-language NAME the ranker
/// compares against — DefaultReleaseParser emits names ("English"/"Spanish"/…), not ISO codes.
/// Generic and BCL-only: names no plugin, interprets a standard HTTP header.</summary>
public static class ContentLanguage
{
    // ISO-639-1 -> the exact English name DefaultReleaseParser emits (keep this list aligned with it).
    private static readonly IReadOnlyDictionary<string, string> CodeToName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German",
            ["it"] = "Italian", ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese",
            ["hi"] = "Hindi", ["ru"] = "Russian", ["pt"] = "Portuguese", ["nl"] = "Dutch",
        };

    /// <summary>The caller's preferred release-language name from Accept-Language, or null when the
    /// header is absent/blank or its language is one we don't map. Accepts "es" and a full
    /// "en-US,en;q=0.9" list (first tag's primary subtag).</summary>
    public static string? FromHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !headers.TryGetValue("Accept-Language", out var value) || string.IsNullOrWhiteSpace(value))
            return null;
        var first = value.Split(',')[0].Split(';')[0].Trim();       // first tag, drop any q-weight
        var primary = first.Split('-')[0].Trim().ToLowerInvariant(); // "en-US" -> "en"
        return CodeToName.TryGetValue(primary, out var name) ? name : null;
    }
}
```

- [ ] **Step 4: Write the mapper test** `EverythingBox.Server.Core.Tests/ContentLanguageTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using EverythingBox.Server.Abstractions;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class ContentLanguageTests
{
    private static Dictionary<string, string> H(string acceptLanguage) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Accept-Language"] = acceptLanguage };

    [Theory]
    [InlineData("es", "Spanish")]
    [InlineData("EN", "English")]
    [InlineData("en-US,en;q=0.9", "English")]   // list + region + q-weight
    [InlineData("pt-BR", "Portuguese")]
    public void MapsAcceptLanguageToReleaseName(string header, string expected)
        => Assert.Equal(expected, ContentLanguage.FromHeaders(H(header)));

    [Fact] public void NullHeadersIsNull() => Assert.Null(ContentLanguage.FromHeaders(null));
    [Fact] public void MissingHeaderIsNull() => Assert.Null(ContentLanguage.FromHeaders(new Dictionary<string, string>()));
    [Fact] public void BlankIsNull() => Assert.Null(ContentLanguage.FromHeaders(H("  ")));
    [Fact] public void UnmappedCodeIsNull() => Assert.Null(ContentLanguage.FromHeaders(H("xx")));
}
```

- [ ] **Step 5: Extend the WithTitle guard.** In `EverythingBox.Server.Core.Tests/MediaRequestWithTitleTests.cs`, find where a request is built with all fields set and `WithTitle` is asserted to preserve them; add `PreferredLanguage = "Spanish"` to the constructed request(s) and `Assert.Equal("Spanish", result.PreferredLanguage);` (matching the existing per-field assertions). If the test iterates subtypes, add the field to each constructed instance and one assertion.

- [ ] **Step 6: Run + commit**

Run: `dotnet test EverythingBox.Server.Core.Tests --filter "ContentLanguageTests|MediaRequestWithTitleTests"`
Expected: PASS.
Run: `dotnet test EverythingBox.Server.Core.Tests`
Expected: green (Abstractions change compiles; `RepositoryCleanlinessTests` + `CoreDependencyTests` unaffected — no new dependency).
```bash
git add EverythingBox.Server.Abstractions/Requests/MediaRequest.cs EverythingBox.Server.Abstractions/Requests/MovieRequest.cs EverythingBox.Server.Abstractions/Requests/TvRequest.cs EverythingBox.Server.Abstractions/Requests/MusicRequest.cs EverythingBox.Server.Abstractions/Requests/AudiobookRequest.cs EverythingBox.Server.Abstractions/Requests/BookRequest.cs EverythingBox.Server.Abstractions/Requests/ComicRequest.cs EverythingBox.Server.Abstractions/Requests/GeneralRequest.cs EverythingBox.Server.Abstractions/Requests/PcGameRequest.cs EverythingBox.Server.Abstractions/ContentLanguage.cs EverythingBox.Server.Core.Tests/ContentLanguageTests.cs EverythingBox.Server.Core.Tests/MediaRequestWithTitleTests.cs
git commit -m "feat: MediaRequest.PreferredLanguage + ContentLanguage (Accept-Language -> release-language name)"
```

---

### Task 2: `DefaultTorrentRanker` folds the per-request language (Core)

**Files:**
- Modify: `EverythingBox.Server.Core/Ranking/DefaultTorrentRanker.cs`
- Modify: `EverythingBox.Server.Core.Tests/TorrentRankerTests.cs`

**Interfaces:**
- Consumes: `MediaRequest.PreferredLanguage` (Task 1).

- [ ] **Step 1: Write the failing test** in `EverythingBox.Server.Core.Tests/TorrentRankerTests.cs` (mirrors the existing `Make`/`SelectBest` fixture; a title containing a language word parses into `info.Languages`):

```csharp
    [Fact]
    public void PerRequestPreferredLanguageBoostsMatchingReleaseOverAnEqualOne()
    {
        var english = Make("The Matrix 1999 English 1080p BluRay x264-A", MediaType.Movie, seeders: 50);
        var spanish = Make("The Matrix 1999 Spanish 1080p BluRay x264-B", MediaType.Movie, seeders: 10);

        // Caller prefers Spanish -> the Spanish release wins despite fewer seeders.
        var best = _ranker.SelectBest(
            new MovieRequest { Title = "The Matrix", PreferredLanguage = "Spanish" },
            [english, spanish], RankingOptions.Default);
        Assert.Equal(spanish.Title, best!.Title);

        // No per-request preference -> today's ranking (higher-seeded English) wins, unchanged.
        var noPref = _ranker.SelectBest(
            new MovieRequest { Title = "The Matrix" }, [english, spanish], RankingOptions.Default);
        Assert.Equal(english.Title, noPref!.Title);
    }
```

- [ ] **Step 2: Run it — expect FAIL** (`MovieRequest` has no `PreferredLanguage` effect yet: the Spanish release does not win).

Run: `dotnet test EverythingBox.Server.Core.Tests --filter PerRequestPreferredLanguageBoostsMatchingReleaseOverAnEqualOne`
Expected: FAIL (best is the English release — the request field is ignored).

- [ ] **Step 3: Fold the request language in `DefaultTorrentRanker.cs`.** In `Score` (~:229-233), replace the two scoring calls so each uses an *effective* preferred list = the request language prepended to the configured one:

```csharp
            if (info is not null)
            {
                Add(LanguageScore(info.Languages, Effective(request.PreferredLanguage, options.PreferredLanguages), request.MediaType), "language");
                if (request.MediaType is MediaType.Movie or MediaType.Tv)
                    Add(SubtitleScore(info.SubtitleLanguages, Effective(request.PreferredLanguage, options.PreferredSubtitleLanguages)), "subtitles");
```

Change `LanguageScore` and `SubtitleScore` to take the effective list instead of `RankingOptions` (they only ever read the two list fields):

```csharp
    private static double LanguageScore(
        IReadOnlyList<string> languages, IReadOnlyList<string> preferred, MediaType mediaType)
    {
        if (preferred.Count == 0 || languages.Count == 0)
            return 0;

        var best = -1;
        foreach (var lang in languages)
        {
            var idx = IndexOfMatch(preferred, lang);
            if (idx >= 0 && (best < 0 || idx < best))
                best = idx;
        }

        if (best >= 0)
            return (preferred.Count - best) * 20;

        if (languages.Any(l => l.Equals("Multi", StringComparison.OrdinalIgnoreCase)))
            return 8;

        return mediaType is MediaType.Book or MediaType.Audiobook ? -100 : -10;
    }

    private static double SubtitleScore(IReadOnlyList<string> subtitles, IReadOnlyList<string> preferred)
    {
        if (preferred.Count == 0 || subtitles.Count == 0)
            return 0;

        var best = -1;
        foreach (var lang in subtitles)
        {
            var idx = IndexOfMatch(preferred, lang);
            if (idx >= 0 && (best < 0 || idx < best))
                best = idx;
        }

        if (best >= 0)
            return (preferred.Count - best) * 15;

        if (subtitles.Any(s => s.Equals("Multi", StringComparison.OrdinalIgnoreCase)))
            return 8;

        return 0;
    }

    // The per-request language (when set and not already leading) prepended to the configured order,
    // deduped case-insensitively. Null/blank request language => the configured list unchanged.
    private static IReadOnlyList<string> Effective(string? requestLanguage, IReadOnlyList<string> configured)
    {
        if (string.IsNullOrWhiteSpace(requestLanguage))
            return configured;
        var list = new List<string>(configured.Count + 1) { requestLanguage };
        foreach (var c in configured)
            if (!string.Equals(c, requestLanguage, StringComparison.OrdinalIgnoreCase))
                list.Add(c);
        return list;
    }
```
(Keep `IndexOfMatch` unchanged. The only edits are the two method signatures, their `options.PreferredX` → `preferred`, the two call sites in `Score`, and the new `Effective` helper.)

- [ ] **Step 4: Run it — expect PASS + full suite**

Run: `dotnet test EverythingBox.Server.Core.Tests --filter PerRequestPreferredLanguageBoostsMatchingReleaseOverAnEqualOne`
Expected: PASS.
Run: `dotnet test EverythingBox.Server.Core.Tests`
Expected: green — every existing ranker test still passes (with no `PreferredLanguage` set, `Effective` returns the configured list unchanged, so scoring is identical to before).

- [ ] **Step 5: Commit**
```bash
git add EverythingBox.Server.Core/Ranking/DefaultTorrentRanker.cs EverythingBox.Server.Core.Tests/TorrentRankerTests.cs
git commit -m "feat: ranker folds MediaRequest.PreferredLanguage ahead of configured audio+subtitle languages"
```

---

### Task 3: Sources stamp the language from `Accept-Language` (Server)

**Files:**
- Modify: `EverythingBox.Server/Sources/MetadataBackedVideoSource.cs` (`ResolveAsync`)
- Modify: `EverythingBox.Server/Sources/IndexerSearchSource.cs` (thread `ctx` into request building)
- Modify: `EverythingBox.Server.Tests/MetadataBackedVideoSourceTests.cs` and/or `IndexerSearchSourceTests.cs`

**Interfaces:**
- Consumes: `ContentLanguage.FromHeaders` (Task 1), `MediaRequest.PreferredLanguage` (Task 1), `SourceContext.RequestHeaders`.

- [ ] **Step 1: `MetadataBackedVideoSource.ResolveAsync`** — `ctx` is already in scope where the request is built (~:159-165). Read the language once and stamp it on each constructed request:

```csharp
        var preferredLanguage = ContentLanguage.FromHeaders(ctx.RequestHeaders);
        MediaRequest? request = decoded.MediaType switch
        {
            MediaType.Movie => new MovieRequest { Title = decoded.Title, Year = decoded.Year, PreferredLanguage = preferredLanguage },
            MediaType.Tv when decoded.Season is { } season && decoded.Episode is { } episode =>
                new TvRequest { Title = decoded.Title, Season = season, Episode = episode, PreferredLanguage = preferredLanguage },
            _ => null,
        };
```
(Add `using EverythingBox.Server.Abstractions;` if not already imported for `ContentLanguage`.)

- [ ] **Step 2: `IndexerSearchSource`** — `SearchAsync` has `ctx` but drops it at the `SearchAsyncCore` boundary. Thread `ctx` (or just the resolved language name) through so the built request carries it. Change:
```csharp
    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => SearchAsyncCore(catalogId, query, ContentLanguage.FromHeaders(ctx.RequestHeaders), ct);

    private async Task<SourceCatalog> SearchAsyncCore(string catalogId, string? query, string? preferredLanguage, CancellationToken ct)
    {
        // ... unchanged up to building the request ...
        var request = BuildRequest(mediaType, query, preferredLanguage);
        // ... unchanged _grabber.SearchRankedAsync(request, ct) ...
    }
```
and give `BuildRequest` a `string? preferredLanguage` parameter, stamping it on whatever `MediaRequest` subtype it constructs (add `PreferredLanguage = preferredLanguage` to the initializer(s) inside `BuildRequest`). Read the real `BuildRequest` body and add the field to each request it builds. `ResolveAsync` in this source does NOT call `SearchRankedAsync` — leave it unchanged.

- [ ] **Step 3: Test that the source stamps the language.** Use the existing fake `ITorrentGrabber` in the source tests (it captures the `MediaRequest` passed to `SearchRankedAsync`). Add a test to whichever suite has that fake (mirror its existing setup):

```csharp
    [Fact]
    public async Task Search_stamps_the_Accept_Language_on_the_request()
    {
        // ... build the source with a capturing fake grabber, as the existing tests do ...
        var ctx = new SourceContext
        {
            RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept-Language"] = "es" }
        };

        await source.SearchAsync("<movies catalog id>", "The Matrix", ctx, CancellationToken.None);

        Assert.Equal("Spanish", capturingGrabber.LastRequest!.PreferredLanguage);
    }
```
For `MetadataBackedVideoSource`, drive `ResolveAsync(<a movie meta id>, 0, ctx, ct)` instead and assert the captured request's `PreferredLanguage == "Spanish"`. If the existing fake grabber doesn't expose the last request, add a minimal `LastRequest` capture to it (test-double only). Also assert the no-header case leaves `PreferredLanguage` null (a bare `SourceContext`).

- [ ] **Step 4: Run + commit**

Run: `dotnet test EverythingBox.Server.Tests --filter "MetadataBackedVideoSourceTests|IndexerSearchSourceTests"`
Expected: PASS incl. the new stamping tests.
Run: `dotnet test EverythingBox.Server.Tests`
Expected: whole host suite green.
```bash
git add EverythingBox.Server/Sources/MetadataBackedVideoSource.cs EverythingBox.Server/Sources/IndexerSearchSource.cs EverythingBox.Server.Tests/MetadataBackedVideoSourceTests.cs EverythingBox.Server.Tests/IndexerSearchSourceTests.cs
git commit -m "feat: movie/TV sources stamp the caller's Accept-Language onto the ranked search request"
```

---

## Integration & live verification (increment done-when — after merge)

- Republish the engine (`dotnet publish EverythingBox.Server -c Release -o out`) and restart the running server (the plugin is unchanged this increment).
- With a debrid key configured (TorBox), resolve a movie/TV title known to have releases in multiple audio languages, once with `Accept-Language: es` and once without, and confirm the ranked candidate order prefers the Spanish release when asked. (Availability-dependent; the unit tests are the deterministic proof — this is the end-to-end sanity check.)
- Then the real client with "Preferred content language = Spanish": a movie search/resolve should prefer Spanish-language releases where they exist.

## Self-review

**Spec coverage (Increment 3):** sources read `Accept-Language` and thread it into the ranker (spec) → Task 3 + Task 1 `ContentLanguage`. Per-request override of `PreferredLanguages`/`PreferredSubtitleLanguages`, both audio and subtitle boosts, 2-letter→name (spec) → Task 2 `Effective` on both `LanguageScore`+`SubtitleScore`, Task 1 mapper. Per-call, not a mutation of global `RankingOptions`; no header → unchanged (spec) → `MediaRequest.PreferredLanguage` field + `Effective` null-guard, Task 2 control assertion. ✅

**Placeholder scan:** all code is complete; the `WithTitle` change enumerates all 8 subtypes with a grep check; the ranker edit lists exactly which lines change; Task 3 points at the real `BuildRequest`/fake-grabber to mirror rather than hand-waving.

**Type consistency:** `MediaRequest.PreferredLanguage : string?` defined in Task 1, read in Task 2 (`Effective(request.PreferredLanguage, …)`) and set in Task 3. `ContentLanguage.FromHeaders(IReadOnlyDictionary<string,string>?) : string?` defined in Task 1, called in Task 3. `LanguageScore(IReadOnlyList<string> languages, IReadOnlyList<string> preferred, MediaType)` / `SubtitleScore(IReadOnlyList<string> subtitles, IReadOnlyList<string> preferred)` / `Effective(string?, IReadOnlyList<string>)` consistent within Task 2. Names are release-language NAMES throughout (mapper output, request field, ranker comparison). ✅
