# Multi-title search (EBS#2): a request carries alternates, fanned out and merged

**Status:** approved 2026-08-12, ready for planning.

## Goal

Let one `MediaRequest` carry a primary `Title` plus `AlternateTitles`, so a set indexed under a
regional/localised/alternate name is found in **one** call, merged and ranked into one best pick —
instead of the caller running N searches and comparing N separately-ranked lists. Entirely
public-repo (`EverythingBoxServer`) and additive; the private provider plugin needs no code change.

## The three things that must move (all public repo)

### 1. `MediaRequest` gains `AlternateTitles` + `WithTitle` (Abstractions)
`MediaRequest` is an `abstract class` with `{ get; init; }` properties (not a record), so:
- Add `public IReadOnlyList<string> AlternateTitles { get; init; } = [];` — additive, source-compatible,
  same shape as the existing `AdditionalTerms`/`ExternalIds` defaults. `Title` stays the primary/display
  name; alternates only widen what counts as a hit.
- Add `public abstract MediaRequest WithTitle(string title);` and implement it in every subclass
  (`MovieRequest`, `TvRequest`, `MusicRequest`, `BookRequest`, `ComicRequest`, `AudiobookRequest`,
  `PcGameRequest`, `GeneralRequest`, and the host-internal `UnknownMediaTypeRequest`). Each returns a
  copy with `Title` replaced and **all other fields copied verbatim** (including its own subtype fields
  and `AlternateTitles`). This is how the grabber queries a provider *under* an alternate title without
  the provider changing — a `switch` in Core can't clone the host-internal request types, so the clone
  must be polymorphic. (New abstract member on a public base ⇒ the API bump below; no plugin subclasses
  `MediaRequest`, so nothing external breaks — subclasses are all in-repo and compiler-enforced.)

### 2. The relevance gate accepts ANY title (`DefaultTorrentRanker`)
`IsRelevant` today derives a single `subject` (`Title`, or `Album ?? Title` for music) and requires
every significant token of it to appear in the release title (subset containment). Change it to accept
a match against the primary **or any** alternate:
- Build a candidate set: for music `{ Album ?? Title } ∪ AlternateTitles`, otherwise `{ Title } ∪
  AlternateTitles` (non-empty, deduped). Relevant iff **any** candidate's tokens are all contained in
  the release tokens. The failure message names the primary subject's missing terms (unchanged shape).
- The season/episode (`TvRequest`), year (movie), and `MediaTypeMatches` checks are title-independent
  and stay exactly as they are — alternates widen only the *title* test, nothing else.
This is the one silent-failure point the issue names: an alternate added to the request but not to the
gate would produce hits that are then all filtered out. A test asserts a release matching ONLY an
alternate passes, and one matching none still fails.

### 3. `TorrentGrabber` fans out over titles × providers, bounded (Core)
Today the loops are provider-only (`SearchInternalAsync` parallel/sequential at the two `Select`/
`foreach` sites, and the same in `QuickGrabAsync`). Change to fan out over **(capable provider) ×
(distinct search title)**:
- Search titles = `{ request.Title } ∪ request.AlternateTitles`, non-empty, `Distinct(OrdinalIgnoreCase)`.
  With no alternates this is exactly one title ⇒ **behaviour identical to today**.
- Each (provider, title) query calls the existing `QueryProviderAsync` with `request.WithTitle(title)`
  (so the provider builds its query from that title). The provider contract is unchanged.
- **Merge + rank use the ORIGINAL `request`** (which carries all titles), so the gate sees every
  alternate. The cloned per-title requests are ephemeral, used only to drive provider queries.
- Existing `Deduplicate` (`DeduplicationKey`: info-hash → magnet → download-url → title+size, keep
  most-seeded) already collapses the same release found under two titles — merging is free, no change.
- **Concurrency cap.** Add `GrabberOptions.MaxConcurrentSearches` (default **8**) and bound the fan-out
  with a `SemaphoreSlim`. N titles × M providers can be large (3×6 = 18); the cap throttles it. Default
  8 leaves single-title/≤8-provider setups running fully concurrently as today. Applies to both the
  normal and quick-grab paths.
- **Quick-grab early-exit still cuts the WHOLE fan-out.** The `QuickGrabAsync` `WhenEach`/re-rank/
  `cts.Cancel()` logic extends to the (provider, title) task set: once a candidate clears the threshold
  (respecting `PreferCachedReleases`/`requireCached`), the cancel stops all remaining title×provider
  queries, not just the remaining providers.

## Tie-break decision

**Best release wins, regardless of which title matched** (approved). The ranker `Score` has no
title-similarity term and never learns which title produced a hit, so a superior release found under an
alternate beats a weaker primary-title release, and equal-quality ties fall to the existing
`log10(seeders)` tiebreak. No title-provenance is threaded onto results; `Score` is **untouched**. Only
the gate (`IsRelevant`) changes.

## Host API

Additive contract surface (`AlternateTitles` property + `WithTitle` abstract member on `MediaRequest`,
`MaxConcurrentSearches` on `GrabberOptions`). Bump `ServerApi.VersionString` **1.15 → 1.16**; update the
two version-pin tests (Minor 16) + add `[InlineData(1, 15)]` to the compat theory. The private plugin
does not subclass `MediaRequest` and needs no code change (it recompiles against 1.16 automatically; its
`ApiVersion` tracks `ServerApi.VersionString`).

## Testing (all public repo)

- **`TorrentRankerTests`** — a release matching ONLY an alternate passes the gate; a release matching
  none is rejected; the primary still works with alternates present; music (Album primary) + alternates;
  alternates do NOT loosen the season/episode, year, or media-type checks (a wrong-season release under
  a matching alternate is still rejected).
- **`GrabberTests`** (+ `RankedSearchTests`) — a request with two titles and a fake provider that returns
  a different release per query term yields a merged list containing both; the same release returned
  under two titles dedupes to one; a request with empty `AlternateTitles` behaves exactly as a
  single-title search (regression); `MaxConcurrentSearches` bounds in-flight queries (a fake provider
  recording max observed concurrency); the quick-grab early-exit cancels remaining title×provider
  queries once the threshold is cleared.
- **`WithTitle`** — each subclass returns a copy with the new `Title` and all other fields (incl. its
  subtype fields, `Year`, `ExternalIds`, `AdditionalTerms`, `AlternateTitles`) preserved.
- Version-pin tests → 1.16; compat theory gains `[InlineData(1, 15)]`.
- No test spawns a process, touches the network, or reads a real browser profile.

## What binds

- **Additive + behaviour-preserving.** Empty `AlternateTitles` ⇒ identical to today (one search title,
  same provider concurrency for ≤8 providers). Nothing reading `Title` changes.
- **The gate is the correctness gate.** Alternates must be accepted in `IsRelevant` or their hits are
  silently filtered — the dedicated ranker test is the guard.
- **Providers unchanged.** Fan-out drives them via `WithTitle`-cloned requests; the provider contract,
  and the private plugin, are untouched.
- **Cleanliness:** no external content-source name; `RepositoryCleanlinessTests` green. No new package.

## Out of scope (deliberate, deferred)

- **Metadata auto-contributing alternates.** `MetadataItem`/`IMetadataSource` carry a single `Title`
  today; harvesting a metadata provider's aliases into `AlternateTitles` is a clean follow-up. This issue is
  **caller-only**: whoever builds the request supplies the alternates.
- **Native multi-term provider capability.** No provider supports one-query-many-terms; adding a
  `ProviderCapabilities` flag now is dead code. The declared extension point: a future flag lets the
  grabber skip per-title splitting for a provider that reads `AlternateTitles` itself.
- Any change to `Score`/title-provenance (the tie-break decision removes the need).

## Done when

- `MediaRequest` carries `AlternateTitles` and every subclass implements `WithTitle`; `IsRelevant`
  accepts a match against the primary or any alternate; `TorrentGrabber` fans out over titles ×
  providers, bounded by `MaxConcurrentSearches`, merged by the existing dedupe, with the quick-grab
  early-exit still cutting the whole fan-out.
- A single-title (empty-alternates) request behaves exactly as today; the ranker/grabber suites prove
  both the new acceptance and the regression. Engine at API 1.16. #2 can be closed.
