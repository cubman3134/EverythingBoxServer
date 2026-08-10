namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Knobs that steer how the ranker filters and scores candidates. Sensible
/// defaults are provided; callers override only what they care about.
/// </summary>
public sealed class RankingOptions
{
    /// <summary>Drop releases with fewer seeders than this.</summary>
    public int MinSeeders { get; init; } = 1;

    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }

    /// <summary>Preferred video resolutions, best first (TV/movies).</summary>
    public IReadOnlyList<VideoResolution> PreferredResolutions { get; init; } = [];

    /// <summary>Preferred audio formats, best first (music).</summary>
    public IReadOnlyList<AudioFormat> PreferredAudioFormats { get; init; } = [];

    /// <summary>Preferred languages, best first. Empty means "don't care".</summary>
    public IReadOnlyList<string> PreferredLanguages { get; init; } = [];

    /// <summary>
    /// Ordered release-group preference, best first (e.g. a repack or scene group name). A
    /// result whose <see cref="ReleaseInfo.ReleaseGroup"/> matches an entry ranks higher;
    /// earlier entries win. Case-insensitive. Empty (the default) leaves ranking unchanged —
    /// an absent or unlisted group is never penalised.
    /// </summary>
    public IReadOnlyList<string> PreferredReleaseGroups { get; init; } = [];

    /// <summary>
    /// Preferred subtitle languages, best first (TV/movies). A release advertising
    /// subtitles in one of these is boosted; empty means "don't care". Values are
    /// language names matching the parser's output, e.g. "English", "French".
    /// </summary>
    public IReadOnlyList<string> PreferredSubtitleLanguages { get; init; } = [];

    /// <summary>Case-insensitive substrings that disqualify a release (e.g. "CAM").</summary>
    public IReadOnlyList<string> BannedTerms { get; init; } = [];

    /// <summary>
    /// When true, a candidate must plausibly match the request (title, and for TV
    /// the season/episode) to be eligible. Guards against providers returning junk.
    /// </summary>
    public bool RequireRelevanceMatch { get; init; } = true;

    public static RankingOptions Default { get; } = new();
}
