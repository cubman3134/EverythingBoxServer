using System.Text.RegularExpressions;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Ranking;

/// <summary>
/// Default scoring engine. Two stages:
/// <list type="number">
///   <item><b>Eligibility</b> — hard filters that drop a candidate entirely:
///   downloadable, min seeders, size bounds, banned terms, and (when
///   <see cref="RankingOptions.RequireRelevanceMatch"/> is set) relevance to the
///   request (title overlap, TV season/episode, movie year).</item>
///   <item><b>Scoring</b> — additive weighting of quality signals from
///   <see cref="TorrentResult.ParsedInfo"/> (resolution/source for video,
///   audio format for music), language preference, proper/repack, with seeders as
///   the tiebreaker.</item>
/// </list>
/// Candidates with no <see cref="TorrentResult.ParsedInfo"/> still rank — they just
/// score on seeders alone — so the ranker is usable without the parser.
/// </summary>
public sealed class DefaultTorrentRanker : ITorrentRanker
{
    public IReadOnlyList<ScoredTorrent> Rank(
        MediaRequest request,
        IEnumerable<TorrentResult> candidates,
        RankingOptions options)
    {
        var scored = new List<ScoredTorrent>();
        foreach (var candidate in candidates)
        {
            var (eligible, _) = IsEligible(request, candidate, options);
            if (!eligible)
                continue;

            var (score, reasons) = Score(request, candidate, options);
            scored.Add(new ScoredTorrent(candidate, score, reasons));
        }

        // Stable order: score desc, then seeders desc as a final tiebreak.
        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : (b.Result.Seeders ?? 0).CompareTo(a.Result.Seeders ?? 0);
        });
        return scored;
    }

    public TorrentResult? SelectBest(
        MediaRequest request,
        IEnumerable<TorrentResult> candidates,
        RankingOptions options)
        => Rank(request, candidates, options) is [var top, ..] ? top.Result : null;

    // --- Eligibility -------------------------------------------------------

    private static (bool Eligible, string Reason) IsEligible(
        MediaRequest request, TorrentResult r, RankingOptions options)
    {
        if (!r.IsDownloadable)
            return (false, "no magnet or download url");

        var seeders = r.Seeders ?? 0;
        if (seeders < options.MinSeeders)
            return (false, $"seeders {seeders} < min {options.MinSeeders}");

        if (options.MaxSizeBytes is { } max && r.SizeBytes > max)
            return (false, "exceeds max size");
        if (options.MinSizeBytes is { } min && r.SizeBytes < min)
            return (false, "below min size");

        foreach (var banned in options.BannedTerms)
            if (r.Title.Contains(banned, StringComparison.OrdinalIgnoreCase))
                return (false, $"banned term '{banned}'");

        if (options.RequireRelevanceMatch && !IsRelevant(request, r, out var why))
            return (false, why);

        return (true, string.Empty);
    }

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
        var evaluatedAny = false;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var candidateTokens = Tokenize(candidate);
            // Tokenize keeps only [a-z0-9] runs of length >1, so a purely non-Latin candidate
            // (CJK/Cyrillic/etc.) tokenizes to the EMPTY set. Empty ⊆ anything would match every
            // release and silently disable the gate, so skip it — like a blank candidate — rather
            // than let it wildcard containment.
            if (candidateTokens.Count == 0) continue;
            evaluatedAny = true;
            var missing = candidateTokens.Where(t => !have.Contains(t)).ToList();
            if (missing.Count == 0) { matched = true; break; }
            primaryMissing ??= missing; // report the primary subject's gap when nothing matches
        }
        // If no candidate was tokenizable at all (every candidate blank or non-Latin — e.g. a
        // purely-CJK request), title relevance can't be assessed: fall back to accept rather than
        // reject every release (which would break legitimate non-Latin-only searches and the
        // pre-existing accept for a blank primary). A tokenizable candidate that didn't match still
        // rejects.
        if (!matched && !evaluatedAny)
            matched = true;
        if (!matched)
        {
            why = $"title missing terms: {string.Join(", ", primaryMissing ?? [])}";
            return false;
        }

        if (!MediaTypeMatches(request.MediaType, r))
        {
            why = $"release looks like the wrong media type for a {request.MediaType} request";
            return false;
        }

        var info = r.ParsedInfo;

        if (request is TvRequest tv && info is not null)
        {
            if (tv.Season is { } season && info.Season is { } parsedSeason && parsedSeason != season)
            {
                why = $"season {parsedSeason} != requested {season}";
                return false;
            }
            if (tv.Episode is { } ep && info.Episodes.Count > 0 && !info.Episodes.Contains(ep))
            {
                why = $"episode {ep} not in release";
                return false;
            }
        }

        // Year mismatch is a strong signal for movies; for TV it is unreliable
        // (air year vs release year), so only reject movies.
        if (request is MovieRequest && request.Year is { } y && info?.Year is { } py && py != y)
        {
            why = $"year {py} != requested {y}";
            return false;
        }

        why = string.Empty;
        return true;
    }

    private static readonly Regex EbookMarker =
        new(@"\b(epub|mobi|azw3?|azw4|pdf|cbr|cbz|djvu|fb2)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AudiobookMarker =
        new(@"\b(audiobooks?|m4b|unabridged|abridged|narrated)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ComicMarker =
        new(@"\b(cbr|cbz|cb7|cbt|manga|comics?|webtoons?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Conservative cross-type filter: drop a release only when it has a *definite*
    /// marker that conflicts with the requested media type (so an audiobook search
    /// won't surface EPUBs, a music search won't surface the same-named movie, etc.).
    /// Ambiguous releases — no strong signal either way — are kept.
    /// </summary>
    private static bool MediaTypeMatches(MediaType requested, TorrentResult r)
    {
        var info = r.ParsedInfo;

        var ebook = EbookMarker.IsMatch(r.Title);
        var audiobook = AudiobookMarker.IsMatch(r.Title);
        var comic = ComicMarker.IsMatch(r.Title);
        var video = info is not null
            && (info.Resolution is not (null or VideoResolution.Unknown)
                || info.VideoCodec is not null
                || info.Episodes.Count > 0
                || IsVideoSource(info.Source));
        var audio = info is not null && info.AudioFormat is not (null or AudioFormat.Unknown);

        return requested switch
        {
            MediaType.Movie or MediaType.Tv => !(ebook || audiobook || (audio && !video)),
            MediaType.Music => !(ebook || audiobook || video),
            MediaType.Audiobook => !(ebook || video),
            MediaType.Book => !(audiobook || video || comic), // prose only — exclude comics
            MediaType.Comic => !(audiobook || video || (audio && !video)),
            _ => true,
        };
    }

    // "web" sources are ambiguous for music releases, so they don't count as video.
    private static bool IsVideoSource(ReleaseSource? source) => source is
        ReleaseSource.Cam or ReleaseSource.Telesync or ReleaseSource.Dvd
        or ReleaseSource.Hdtv or ReleaseSource.BluRay or ReleaseSource.Remux;

    // --- Scoring -----------------------------------------------------------

    private static (double Score, IReadOnlyList<string> Reasons) Score(
        MediaRequest request, TorrentResult r, RankingOptions options)
    {
        double score = 0;
        var reasons = new List<string>();
        var info = r.ParsedInfo;

        void Add(double points, string reason)
        {
            if (points == 0) return;
            score += points;
            reasons.Add($"{(points > 0 ? "+" : "")}{points:0.#} {reason}");
        }

        var isAudio = request.MediaType is MediaType.Music or MediaType.Audiobook;

        if (info is not null && !isAudio)
        {
            Add(ResolutionScore(info.Resolution, options), $"resolution {info.Resolution}");
            Add(SourceScore(info.Source), $"source {info.Source}");
        }
        else if (info is not null)
        {
            Add(AudioFormatScore(info.AudioFormat, options), $"format {info.AudioFormat}");
        }

        if (info is not null)
        {
            Add(LanguageScore(info.Languages, Effective(request.PreferredLanguage, options.PreferredLanguages), request.MediaType), "language");
            if (request.MediaType is MediaType.Movie or MediaType.Tv)
                Add(SubtitleScore(info.SubtitleLanguages, Effective(request.PreferredLanguage, options.PreferredSubtitleLanguages)), "subtitles");
            if (info.IsProper) Add(5, "proper");
            if (info.IsRepack) Add(5, "repack");
            Add(ReleaseGroupScore(info.ReleaseGroup, options), $"release group {info.ReleaseGroup}");
            if (request is TvRequest { FullSeason: true } && info.Episodes.Count > 0)
                Add(-15, "single episode but full season wanted");
            if (request.Year is { } y && info.Year == y)
                Add(5, "year match");
        }

        // Seeders: always present, kept small so it breaks ties rather than
        // overriding quality.
        var seeders = r.Seeders ?? 0;
        Add(Math.Log10(seeders + 1) * 5, $"seeders {seeders}");

        return (score, reasons);
    }

    private static double ResolutionScore(VideoResolution? res, RankingOptions options)
    {
        if (res is null or VideoResolution.Unknown)
            return 0;

        if (options.PreferredResolutions.Count > 0)
        {
            var idx = IndexOf(options.PreferredResolutions, res.Value);
            return idx >= 0 ? (options.PreferredResolutions.Count - idx) * 30 : -25;
        }

        // No preference: higher resolution wins by default.
        return (int)res.Value * 8;
    }

    private static double ReleaseGroupScore(string? group, RankingOptions options)
    {
        if (options.PreferredReleaseGroups.Count == 0 || string.IsNullOrWhiteSpace(group))
            return 0; // no preference, or no group to match → neutral, never a penalty

        var idx = IndexOfMatch(options.PreferredReleaseGroups, group);
        return idx >= 0 ? (options.PreferredReleaseGroups.Count - idx) * 20 : 0; // unlisted → neutral
    }

    private static double SourceScore(ReleaseSource? source) => source switch
    {
        ReleaseSource.Remux => 30,
        ReleaseSource.BluRay => 28,
        ReleaseSource.WebDl => 22,
        ReleaseSource.WebRip => 18,
        ReleaseSource.Hdtv => 12,
        ReleaseSource.Dvd => 8,
        ReleaseSource.Telesync => 2,
        ReleaseSource.Cam => 0,
        _ => 5,
    };

    private static double AudioFormatScore(AudioFormat? format, RankingOptions options)
    {
        if (format is null or AudioFormat.Unknown)
            return 0;

        if (options.PreferredAudioFormats.Count > 0)
        {
            var idx = IndexOf(options.PreferredAudioFormats, format.Value);
            return idx >= 0 ? (options.PreferredAudioFormats.Count - idx) * 30 : -25;
        }

        return format switch
        {
            AudioFormat.Flac => 30,
            AudioFormat.Alac => 28,
            AudioFormat.Aac => 18,
            AudioFormat.Vorbis => 15,
            AudioFormat.Mp3 => 12,
            _ => 0,
        };
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

        // A generic multi-subtitle release usually includes the major languages.
        if (subtitles.Any(s => s.Equals("Multi", StringComparison.OrdinalIgnoreCase)))
            return 8;

        return 0;
    }

    private static double LanguageScore(
        IReadOnlyList<string> languages, IReadOnlyList<string> preferred, MediaType mediaType)
    {
        // No language tag means the default (English) edition - the common case for English releases, which
        // rarely advertise their language. Take no opinion rather than penalise it.
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

        // A generic multi-language release usually includes the preferred language.
        if (languages.Any(l => l.Equals("Multi", StringComparison.OrdinalIgnoreCase)))
            return 8;

        // Tagged with a language we don't want. For prose/audiobooks a foreign edition is a different book
        // entirely (a translation), so penalise hard - an English/untagged release should always win, while
        // the foreign one stays eligible as a last resort. For video it's a milder signal (a dub), so keep
        // the existing small penalty.
        return mediaType is MediaType.Book or MediaType.Audiobook ? -100 : -10;
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

    private static HashSet<string> Tokenize(string text)
        => TokenSplitter.Split(text.ToLowerInvariant())
            .Where(t => t.Length > 1)
            .ToHashSet();

    private static readonly Regex TokenSplitter = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
            if (EqualityComparer<T>.Default.Equals(list[i], value))
                return i;
        return -1;
    }

    private static int IndexOfMatch(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
