using System.Text.RegularExpressions;

namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Regex-based release-title parser. Extracts resolution, source, codecs,
/// season/episode, year, language, group, and music format from scene / p2p
/// naming conventions. Every field is best-effort: unknowns are left null/empty
/// rather than guessed. Provider-agnostic and pure, so it is cheap to unit test.
/// </summary>
public sealed class DefaultReleaseParser : IReleaseParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Normalization
    private static readonly Regex Separators = new(@"[._]+", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex Extension = new(@"\.(mkv|mp4|avi|m4v|ts|flac|mp3|m4a)$", Opts);

    // Structural tokens
    private static readonly Regex Year = new(@"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Resolution = new(@"\b(2160p|4k|uhd|1080[pi]|720p|576p|480p)\b", Opts);
    private static readonly Regex SeasonEpisode = new(@"\bS(?<s>\d{1,2})(?<e>(?:E\d{1,3})(?:[-E]+\d{1,3})*)\b", Opts);
    private static readonly Regex SeasonOnly = new(@"\b(?:Season ?(?<s1>\d{1,2})|S(?<s2>\d{1,2})(?![\dE]))\b", Opts);
    private static readonly Regex AltEpisode = new(@"\b(?<s>\d{1,2})x(?<e>\d{1,3})\b", Opts);
    private static readonly Regex Numbers = new(@"\d{1,3}", RegexOptions.Compiled);

    // Provenance
    private static readonly Regex GroupTrailing = new(@"-(?<g>[A-Za-z0-9]+)$", RegexOptions.Compiled);
    private static readonly Regex GroupBracket = new(@"^\[(?<g>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex Proper = new(@"\bPROPER\b", Opts);
    private static readonly Regex Repack = new(@"\bREPACK\b", Opts);

    // Codecs / audio
    private static readonly Regex VideoCodec = new(@"\b(x265|x264|h ?265|h ?264|hevc|avc|av1|xvid|divx|mpeg ?2)\b", Opts);
    private static readonly Regex AudioFormatToken = new(@"\b(flac|alac|mp3|aac|m4a|ogg|vorbis|wav)\b", Opts);
    private static readonly Regex Mp3Bitrate = new(@"\b(320|256|224|192|160|128)\b", RegexOptions.Compiled);

    // Tags that name a source outright. Most specific first: the first match wins, so a
    // release carrying two of them is reported as the more specific one.
    private static readonly (Regex Rx, ReleaseSource Source)[] Sources =
    [
        (new(@"\bremux\b", Opts), ReleaseSource.Remux),
        (new(@"\b(?:blu-?ray|bd-?rip|br-?rip|bd(?:25|50)?)\b", Opts), ReleaseSource.BluRay),
        (new(@"\bweb-?dl\b", Opts), ReleaseSource.WebDl),
        (new(@"\bweb-?rip\b", Opts), ReleaseSource.WebRip),
        (new(@"\b(?:hdtv|pdtv|sdtv|dsr)\b", Opts), ReleaseSource.Hdtv),
        (new(@"\b(?:dvd-?rip|dvd-?r|dvd)\b", Opts), ReleaseSource.Dvd),
        (new(@"\b(?:telesync|hd-?ts)\b", Opts), ReleaseSource.Telesync),
        (new(@"\bhd-?cam\b", Opts), ReleaseSource.Cam),
    ];

    // Bare words that can name a source but also turn up innocently: a film can be called
    // "Charlotte's Web", a store-bought album is tagged WEB, and "ts" and "cam" are everyday
    // abbreviations. These are consulted only when nothing above matched, so a word that
    // merely appears in a title never outranks a tag that designates the rip -- which is why
    // "Charlotte's Web[2006]DvDrip" reports as a DVD rip rather than as a web release. Their
    // order among themselves is the order they held in the single list they came from.
    private static readonly (Regex Rx, ReleaseSource Source)[] AmbiguousSources =
    [
        (new(@"\bweb\b", Opts), ReleaseSource.WebDl),
        (new(@"\bts\b", Opts), ReleaseSource.Telesync),
        (new(@"\bcam\b", Opts), ReleaseSource.Cam),
    ];

    private static readonly (Regex Rx, string Label)[] AudioCodecs =
    [
        (new(@"\batmos\b", Opts), "Atmos"),
        (new(@"\btrue-?hd\b", Opts), "TrueHD"),
        (new(@"\bdts-?hd\b", Opts), "DTS-HD"),
        (new(@"\bdts\b", Opts), "DTS"),
        (new(@"\b(?:eac3|dd\+|ddp)\b", Opts), "EAC3"),
        (new(@"\b(?:ac3|dd5\.?1|dd)\b", Opts), "AC3"),
        (new(@"\baac\b", Opts), "AAC"),
        (new(@"\bflac\b", Opts), "FLAC"),
    ];

    private static readonly (Regex Rx, string Label)[] Languages =
    [
        (new(@"\bmulti\b", Opts), "Multi"),
        (new(@"\benglish\b", Opts), "English"),
        (new(@"\bfrench\b", Opts), "French"),
        (new(@"\bgerman\b", Opts), "German"),
        (new(@"\b(?:spanish|espa[nñ]ol|castellano)\b", Opts), "Spanish"),
        (new(@"\bitalian\b", Opts), "Italian"),
        (new(@"\bjapanese\b", Opts), "Japanese"),
        (new(@"\bkorean\b", Opts), "Korean"),
        (new(@"\b(?:chinese|mandarin|cantonese)\b", Opts), "Chinese"),
        (new(@"\bhindi\b", Opts), "Hindi"),
        (new(@"\brussian\b", Opts), "Russian"),
        (new(@"\bportuguese\b", Opts), "Portuguese"),
        (new(@"\bdutch\b", Opts), "Dutch"),
    ];

    // Subtitle markers → the language they indicate. "Multi" is a generic
    // multi-subtitle release (language unspecified, but usually includes the majors).
    private static readonly (Regex Rx, string Label)[] Subtitles =
    [
        (new(@"\bvostfr\b", Opts), "French"),
        (new(@"\blegendado\b", Opts), "Portuguese"),
        (new(@"\bsubtitulado\b", Opts), "Spanish"),
        (new(@"\besubs?\b", Opts), "English"),
        (new(@"\b(?:eng|english)[ .\-]?subs?\b", Opts), "English"),
        (new(@"\bsubs?[ .\-]?(?:eng|english)\b", Opts), "English"),
        (new(@"\b(?:fre|french)[ .\-]?subs?\b", Opts), "French"),
        (new(@"\b(?:ger|german)[ .\-]?subs?\b", Opts), "German"),
        (new(@"\b(?:spa|spanish|esp)[ .\-]?subs?\b", Opts), "Spanish"),
        (new(@"\b(?:ita|italian)[ .\-]?subs?\b", Opts), "Italian"),
        (new(@"\b(?:por|portuguese|pt)[ .\-]?subs?\b", Opts), "Portuguese"),
        (new(@"\b(?:multi|m)[ .\-]?subs?\b", Opts), "Multi"),
    ];

    public ReleaseInfo Parse(string releaseTitle, MediaType mediaType)
    {
        if (string.IsNullOrWhiteSpace(releaseTitle))
            return new ReleaseInfo();

        var working = Extension.Replace(releaseTitle.Trim(), string.Empty);
        var spaced = Separators.Replace(working, " ");

        var (season, episodes) = ParseSeasonEpisode(spaced);
        var year = ParseYear(spaced);
        var (audioFormat, bitrate) = ParseAudioFormat(spaced);

        return new ReleaseInfo
        {
            NormalizedTitle = NormalizeTitle(spaced),
            Year = year,
            Season = season,
            Episodes = episodes,
            Resolution = ParseResolution(spaced),
            Source = ParseSource(spaced),
            VideoCodec = First(VideoCodec, spaced)?.Replace(" ", string.Empty).ToLowerInvariant() switch
            {
                "h265" or "hevc" => "x265",
                "h264" or "avc" => "x264",
                null => null,
                var v => Canonical(v),
            },
            AudioCodec = ParseAudioCodec(spaced),
            AudioFormat = audioFormat,
            AudioBitrateKbps = bitrate,
            ReleaseGroup = ParseGroup(working),
            Languages = ParseLanguages(spaced),
            SubtitleLanguages = ParseSubtitles(spaced),
            IsProper = Proper.IsMatch(spaced),
            IsRepack = Repack.IsMatch(spaced),
        };
    }

    private static string? First(Regex rx, string input)
    {
        var m = rx.Match(input);
        return m.Success ? m.Value : null;
    }

    private static string Canonical(string lowered) => lowered switch
    {
        "xvid" => "XviD",
        "divx" => "DivX",
        "av1" => "AV1",
        "mpeg2" => "MPEG-2",
        _ => lowered,
    };

    private static int? ParseYear(string spaced)
        => Year.Match(spaced) is { Success: true } m ? int.Parse(m.Value) : null;

    private static VideoResolution? ParseResolution(string spaced)
    {
        var m = Resolution.Match(spaced);
        if (!m.Success)
            return null;

        var v = m.Value.ToLowerInvariant();
        if (v.StartsWith("2160") || v is "4k" or "uhd") return VideoResolution.R2160p;
        if (v.StartsWith("1080")) return VideoResolution.R1080p;
        if (v.StartsWith("720")) return VideoResolution.R720p;
        if (v.StartsWith("576")) return VideoResolution.R576p;
        return VideoResolution.R480p;
    }

    private static ReleaseSource? ParseSource(string spaced)
    {
        foreach (var (rx, source) in Sources)
            if (rx.IsMatch(spaced))
                return source;
        foreach (var (rx, source) in AmbiguousSources)
            if (rx.IsMatch(spaced))
                return source;
        return null;
    }

    private static string? ParseAudioCodec(string spaced)
    {
        foreach (var (rx, label) in AudioCodecs)
            if (rx.IsMatch(spaced))
                return label;
        return null;
    }

    private static (AudioFormat? Format, int? Bitrate) ParseAudioFormat(string spaced)
    {
        var token = First(AudioFormatToken, spaced)?.ToLowerInvariant();
        return token switch
        {
            "flac" => (AudioFormat.Flac, null),
            "alac" => (AudioFormat.Alac, null),
            "aac" or "m4a" => (AudioFormat.Aac, null),
            "ogg" or "vorbis" => (AudioFormat.Vorbis, null),
            "mp3" => (AudioFormat.Mp3, Mp3Bitrate.Match(spaced) is { Success: true } b ? int.Parse(b.Value) : null),
            _ => (null, null),
        };
    }

    private static (int? Season, IReadOnlyList<int> Episodes) ParseSeasonEpisode(string spaced)
    {
        if (SeasonEpisode.Match(spaced) is { Success: true } se)
        {
            var season = int.Parse(se.Groups["s"].Value);
            var part = se.Groups["e"].Value;
            var nums = Numbers.Matches(part).Select(m => int.Parse(m.Value)).ToList();

            if (part.Contains('-') && nums is [var lo, var hi] && hi > lo)
                return (season, Enumerable.Range(lo, hi - lo + 1).ToList());

            return (season, nums);
        }

        if (AltEpisode.Match(spaced) is { Success: true } alt)
            return (int.Parse(alt.Groups["s"].Value), [int.Parse(alt.Groups["e"].Value)]);

        if (SeasonOnly.Match(spaced) is { Success: true } so)
        {
            var s = so.Groups["s1"].Success ? so.Groups["s1"].Value : so.Groups["s2"].Value;
            return (int.Parse(s), []);
        }

        return (null, []);
    }

    private static IReadOnlyList<string> ParseLanguages(string spaced)
    {
        var found = new List<string>();
        foreach (var (rx, label) in Languages)
            if (rx.IsMatch(spaced) && !found.Contains(label))
                found.Add(label);
        return found;
    }

    private static IReadOnlyList<string> ParseSubtitles(string spaced)
    {
        var found = new List<string>();
        foreach (var (rx, label) in Subtitles)
            if (rx.IsMatch(spaced) && !found.Contains(label))
                found.Add(label);
        return found;
    }

    private static string? ParseGroup(string working)
    {
        if (GroupTrailing.Match(working) is { Success: true } t)
            return t.Groups["g"].Value;
        if (GroupBracket.Match(working) is { Success: true } b)
            return b.Groups["g"].Value;
        return null;
    }

    /// <summary>
    /// The work title: everything before the first metadata token (year, season,
    /// resolution, source, codec, or audio format), with separators normalized.
    /// </summary>
    private static string NormalizeTitle(string spaced)
    {
        var cut = spaced.Length;
        foreach (var rx in new[] { SeasonEpisode, AltEpisode, SeasonOnly, Year, Resolution, VideoCodec, AudioFormatToken })
            if (rx.Match(spaced) is { Success: true } m && m.Index < cut)
                cut = m.Index;
        foreach (var (rx, _) in Sources)
            if (rx.Match(spaced) is { Success: true } m && m.Index < cut)
                cut = m.Index;

        var title = GroupBracket.Replace(spaced[..cut], string.Empty);
        title = MultiSpace.Replace(title, " ").Trim().TrimEnd('-', '(', '[', ']', ')').Trim();
        return title.Length > 0 ? title : spaced.Trim();
    }
}
