using System.Text.RegularExpressions;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Parsing;

namespace EverythingBox.Server.Core.Selection;

/// <summary>
/// Narrows the per-file links a debrid service returns for a release down to the
/// specific file the request asked for — the single episode out of a season pack,
/// or the single track out of an album. When the request isn't episode/track
/// specific (or nothing matches), the files are returned unchanged.
/// </summary>
public static class MediaFileMatcher
{
    private static readonly IReleaseParser SharedParser = new DefaultReleaseParser();
    private static readonly Regex TokenSplitter = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^\D*(\d{1,3})(?!\d)", RegexOptions.Compiled);

    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv"];
    private static readonly string[] AudioExtensions = [".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".alac"];
    private static readonly string[] BookExtensions = [".epub", ".pdf", ".mobi", ".azw3", ".azw", ".azw4", ".cbz", ".cbr", ".djvu", ".fb2", ".lit", ".txt"];
    private static readonly string[] ComicExtensions = [".cbz", ".cbr", ".cb7", ".cbt", ".pdf", ".epub"];
    private static readonly Regex NumberPattern = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// Region / language / dump-version tokens that distinguish editions of the SAME work,
    /// not different works. Ignored when measuring how closely a filename matches a title,
    /// so a heavily-tagged release isn't ranked below a sparsely-tagged one. Shared
    /// so remote-file resolution and ROM ranking share one definition.
    /// </summary>
    public static readonly HashSet<string> EditionTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "usa", "ntsc", "pal", "world", "eu", "ue", "europe", "japan", "jp", "ja", "korea",
        "ko", "china", "zh", "taiwan", "asia", "australia", "spain", "es", "france", "fr",
        "germany", "de", "ger", "italy", "it", "netherlands", "nl", "sweden", "sv", "sw",
        "brazil", "br", "russia", "ru", "poland", "pl", "en", "pt", "multi",
        "rev", "beta", "proto", "prototype", "unl", "demo", "kiosk", "sample", "alt",
        "aftermarket", "vc", "virtual", "console",
    };

    /// <summary>
    /// Select the file(s) matching the request's specific episode or track, ordered
    /// largest-first (so the feature file wins over samples). Returns the input
    /// unchanged when there's a single file, the request isn't specific, or nothing
    /// matched.
    /// </summary>
    public static IReadOnlyList<DebridLink> SelectForRequest(
        MediaRequest request, IReadOnlyList<DebridLink> files, IReleaseParser? parser = null)
        => Select(request, files, f => f.FileName, f => f.SizeBytes, parser);

    /// <summary>
    /// Generic core: select the file(s) matching the request's specific episode or
    /// track from any list, given accessors for each item's filename and size.
    /// Used both to filter resolved debrid links and to pre-select which files a
    /// debrid service should download.
    /// </summary>
    public static IReadOnlyList<T> Select<T>(
        MediaRequest request,
        IReadOnlyList<T> files,
        Func<T, string> nameOf,
        Func<T, long?> sizeOf,
        IReleaseParser? parser = null)
    {
        if (files.Count <= 1)
            return files;

        var matched = Match(request, files, nameOf, sizeOf, parser ?? SharedParser);
        if (matched.Count > 0)
            return matched;

        // When the caller asked for a specific unit (episode/track/issue) but nothing
        // matched, returning everything would dump the whole pack — so download nothing.
        // For non-specific requests, fall back to the full list.
        return IsSpecific(request) ? matched : files;
    }

    /// <summary>
    /// Like <see cref="Select{T}"/>, but never falls back to the full list: returns
    /// only the files that actually matched the request (possibly empty), and matches
    /// even a single-file list. Use when "no match" must mean "take nothing" — e.g.
    /// picking one file out of a large archive index where returning everything would
    /// be wrong.
    /// </summary>
    public static IReadOnlyList<T> SelectStrict<T>(
        MediaRequest request,
        IReadOnlyList<T> files,
        Func<T, string> nameOf,
        Func<T, long?> sizeOf,
        IReleaseParser? parser = null)
        => Match(request, files, nameOf, sizeOf, parser ?? SharedParser);

    private static IReadOnlyList<T> Match<T>(
        MediaRequest request, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf, IReleaseParser parser)
        => request switch
        {
            TvRequest { Episode: { } } tv => MatchEpisode(tv, files, nameOf, sizeOf, parser),
            MusicRequest { Track: { } track } when !string.IsNullOrWhiteSpace(track) => MatchTrack(track, files, nameOf, sizeOf),
            BookRequest book => MatchBook(book, files, nameOf, sizeOf),
            ComicRequest comic => MatchComic(comic, files, nameOf, sizeOf),
            GeneralRequest general => MatchGeneral(general, files, nameOf, sizeOf),
            _ => files,
        };

    /// <summary>
    /// Whether the request pins down a single unit within a pack. When true and no
    /// file matches, we return nothing rather than the entire pack.
    /// </summary>
    private static bool IsSpecific(MediaRequest request) => request switch
    {
        TvRequest { Episode: { } } => true,
        MusicRequest { Track: { } t } => !string.IsNullOrWhiteSpace(t),
        ComicRequest { Issue: { } } or ComicRequest { Volume: { } } or ComicRequest { Chapter: { } } => true,
        _ => false,
    };

    private static IReadOnlyList<T> MatchEpisode<T>(
        TvRequest tv, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf, IReleaseParser parser)
    {
        var matches = new List<T>();
        foreach (var file in files)
        {
            var fileName = nameOf(file);
            if (!HasExtension(fileName, VideoExtensions))
                continue;

            var name = BaseName(fileName);
            if (IsSample(name))
                continue;

            var info = parser.Parse(name, MediaType.Tv);
            if (tv.Season is { } season && info.Season is { } parsedSeason && parsedSeason != season)
                continue;
            if (info.Episodes.Contains(tv.Episode!.Value))
                matches.Add(file);
        }

        return OrderBySizeDescending(matches, sizeOf);
    }

    private static IReadOnlyList<T> MatchTrack<T>(
        string track, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf)
    {
        track = track.Trim();
        var wantedNumber = int.TryParse(track, out var n) ? n : (int?)null;
        var wantedTokens = Tokenize(track);

        var matches = new List<T>();
        foreach (var file in files)
        {
            var fileName = nameOf(file);
            if (!HasExtension(fileName, AudioExtensions))
                continue;

            var name = BaseName(fileName);
            if (IsSample(name))
                continue;

            if (wantedNumber is { } number)
            {
                if (LeadingTrackNumber(name) == number)
                    matches.Add(file);
            }
            else if (wantedTokens.Count > 0 && wantedTokens.IsSubsetOf(Tokenize(name)))
            {
                matches.Add(file);
            }
        }

        return OrderBySizeDescending(matches, sizeOf);
    }

    private static IReadOnlyList<T> MatchBook<T>(
        BookRequest book, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf)
    {
        var wanted = Tokenize(book.Title);
        if (wanted.Count == 0)
            return [];

        // Book-format files whose name contains every requested title token, with
        // how many *extra* tokens each has beyond the title.
        var candidates = new List<(T File, string Name, int Extra)>();
        foreach (var file in files)
        {
            var fileName = nameOf(file);
            if (!HasExtension(fileName, BookExtensions))
                continue;

            var name = BaseName(fileName);
            if (IsSample(name))
                continue;

            var tokens = Tokenize(name);
            if (!wanted.IsSubsetOf(tokens))
                continue;

            candidates.Add((file, name, tokens.Count - wanted.Count));
        }

        if (candidates.Count == 0)
            return [];

        // Honor a requested format (e.g. EPUB) when any candidate has it.
        if (!string.IsNullOrWhiteSpace(book.Format))
        {
            var ext = "." + book.Format.Trim().TrimStart('.').ToLowerInvariant();
            var formatMatches = candidates.Where(c => c.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();
            if (formatMatches.Count > 0)
                candidates = formatMatches;
        }

        // Closest title wins: fewest extra tokens, so "Dune" beats "Dune Messiah".
        var minExtra = candidates.Min(c => c.Extra);
        return candidates
            .Where(c => c.Extra == minExtra)
            .OrderByDescending(c => sizeOf(c.File) ?? 0)
            .Select(c => c.File)
            .ToList();
    }

    private static IReadOnlyList<T> MatchComic<T>(
        ComicRequest comic, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf)
    {
        var wanted = Tokenize(comic.Title);
        if (wanted.Count == 0)
            return [];

        // The comic-format files for this series (right extension, not a sample,
        // and whose name contains every requested title token).
        var series = new List<(T File, string Name)>();
        foreach (var file in files)
        {
            var fileName = nameOf(file);
            if (!HasExtension(fileName, ComicExtensions))
                continue;

            var name = BaseName(fileName);
            if (IsSample(name))
                continue;

            if (!wanted.IsSubsetOf(Tokenize(name)))
                continue;

            series.Add((file, name));
        }

        // Honor a requested format (e.g. CBZ) when any series file has it.
        if (!string.IsNullOrWhiteSpace(comic.Format))
        {
            var ext = "." + comic.Format.Trim().TrimStart('.').ToLowerInvariant();
            var formatMatches = series.Where(c => c.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)).ToList();
            if (formatMatches.Count > 0)
                series = formatMatches;
        }

        if (series.Count == 0)
            return [];

        // Try the requested units in order of specificity. A chapter-organized pack
        // resolves to the chapter file; a per-volume pack has no chapter file, so we
        // fall through to the volume (chapter 181 lives inside volume 22). The first
        // unit that matches at least one file wins.
        var hasNumber = false;
        foreach (var number in new int?[] { comic.Chapter, comic.Issue, comic.Volume })
        {
            if (number is not { } n)
                continue;
            hasNumber = true;

            var hits = series.Where(c => ContainsNumber(c.Name, n)).ToList();
            if (hits.Count > 0)
                return Closest(hits, wanted, sizeOf);
        }

        // A specific unit was requested but no file carries that number: download
        // nothing rather than the entire pack.
        if (hasNumber)
            return [];

        // No unit requested — return the closest title match among the series files.
        return Closest(series, wanted, sizeOf);
    }

    /// <summary>
    /// Pick the file(s) whose name is closest to the wanted title tokens — fewest
    /// extra tokens, then largest size — from a list of series candidates.
    /// </summary>
    private static IReadOnlyList<T> Closest<T>(
        List<(T File, string Name)> candidates, HashSet<string> wanted, Func<T, long?> sizeOf)
    {
        var scored = candidates
            .Select(c => (c.File, Extra: Tokenize(c.Name).Count - wanted.Count))
            .ToList();
        var minExtra = scored.Min(c => c.Extra);
        return scored
            .Where(c => c.Extra == minExtra)
            .OrderByDescending(c => sizeOf(c.File) ?? 0)
            .Select(c => c.File)
            .ToList();
    }

    private static IReadOnlyList<T> MatchGeneral<T>(
        GeneralRequest request, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf)
    {
        var extensions = NormalizeExtensions(request);

        // Each tag targets a file inside a pack independently of the item search;
        // fall back to the query title when no tags are given.
        var tags = request.FileFilters.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (tags.Count == 0)
            tags = [request.Title];

        // A file is selected if it matches ANY tag (each tag picks its closest file).
        var result = new List<T>();
        var seen = new HashSet<T>();
        foreach (var tag in tags)
            foreach (var file in MatchOneTag(tag, extensions, files, nameOf, sizeOf))
                if (seen.Add(file))
                    result.Add(file);

        return result;
    }

    private static IReadOnlyList<T> MatchOneTag<T>(
        string tag, IReadOnlyList<string> extensions, IReadOnlyList<T> files, Func<T, string> nameOf, Func<T, long?> sizeOf)
    {
        var wanted = Tokenize(tag);
        if (wanted.Count == 0 && extensions.Count == 0)
            return [];

        var candidates = new List<(T File, int Extra)>();
        foreach (var file in files)
        {
            var name = BaseName(nameOf(file));
            if (IsSample(name))
                continue;
            if (extensions.Count > 0 && !extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                continue;

            var extra = 0;
            if (wanted.Count > 0)
            {
                var tokens = Tokenize(name);
                if (!wanted.IsSubsetOf(tokens))
                    continue;
                // Don't count region/language/version tags as "extra" — otherwise a fully
                // tagged release ("Game (USA, Australia) (En,Fr,De,Es,It)") loses to a
                // sparsely tagged one ("Game (Japan)") of the same title.
                extra = tokens.Count(t => !wanted.Contains(t) && !EditionTokens.Contains(t));
            }

            candidates.Add((file, extra));
        }

        if (candidates.Count == 0)
            return [];

        var minExtra = candidates.Min(c => c.Extra);
        return candidates
            .Where(c => c.Extra == minExtra)
            .OrderByDescending(c => sizeOf(c.File) ?? 0)
            .Select(c => c.File)
            .ToList();
    }

    private static string? NormalizeExtension(string? format)
        => string.IsNullOrWhiteSpace(format) ? null : "." + format.Trim().TrimStart('.').ToLowerInvariant();

    // The acceptable extensions for a general request: FileTypes when set, else the
    // single FileType. Normalized to lower-case, dot-prefixed (".sfc").
    private static IReadOnlyList<string> NormalizeExtensions(GeneralRequest request)
    {
        var list = request.FileTypes
            .Select(NormalizeExtension)
            .Where(e => e is not null)
            .Select(e => e!)
            .Distinct()
            .ToList();
        if (list.Count == 0 && NormalizeExtension(request.FileType) is { } single)
            list.Add(single);
        return list;
    }

    private static bool ContainsNumber(string name, int target)
        => NumberPattern.Matches(name).Any(m => int.TryParse(m.Value, out var n) && n == target);

    private static int? LeadingTrackNumber(string name)
        => LeadingNumber.Match(name) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    // Drop single-letter noise, but KEEP single digits — a sequel/volume number like the
    // "3" in "Super Mario Bros 3" is significant (else it matches "Super Mario Bros" too).
    private static HashSet<string> Tokenize(string text)
        => TokenSplitter.Split(text.ToLowerInvariant())
            // "&" is punctuation and tokenizes to nothing, so drop the word "and" too — that way a query
            // "Sonic and Knuckles" matches the file "Sonic & Knuckles". Applied to both wanted and candidate.
            .Where(t => (t.Length > 1 || (t.Length == 1 && char.IsDigit(t[0]))) && t != "and")
            .ToHashSet();

    private static string BaseName(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static bool HasExtension(string name, string[] extensions)
        => extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsSample(string name)
        => name.Contains("sample", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<T> OrderBySizeDescending<T>(List<T> items, Func<T, long?> sizeOf)
    {
        items.Sort((a, b) => (sizeOf(b) ?? 0).CompareTo(sizeOf(a) ?? 0));
        return items;
    }
}
