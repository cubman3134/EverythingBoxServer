namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Metadata parsed out of a raw release title by an
/// <see cref="IReleaseParser"/>. Every field is best-effort and may
/// be null/empty when the parser cannot determine it. Used by the ranker to weigh
/// quality and to confirm a release actually matches the request.
/// </summary>
public sealed class ReleaseInfo
{
    /// <summary>Cleaned-up work title with scene tokens stripped.</summary>
    public string? NormalizedTitle { get; init; }

    public int? Year { get; init; }

    // --- TV ---
    public int? Season { get; init; }
    public IReadOnlyList<int> Episodes { get; init; } = [];

    // --- Video ---
    public VideoResolution? Resolution { get; init; }
    public ReleaseSource? Source { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }

    // --- Music / audio ---
    public AudioFormat? AudioFormat { get; init; }
    public int? AudioBitrateKbps { get; init; }

    // --- Provenance ---
    public string? ReleaseGroup { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>
    /// Subtitle languages advertised in the title (e.g. "English" from "ESubs",
    /// "French" from "VOSTFR"). "Multi" marks a generic multi-subtitle release.
    /// </summary>
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];

    public bool IsProper { get; init; }
    public bool IsRepack { get; init; }
}

public enum VideoResolution
{
    Unknown,
    R480p,
    R576p,
    R720p,
    R1080p,
    R2160p,
}

public enum ReleaseSource
{
    Unknown,
    Cam,
    Telesync,
    Dvd,
    Hdtv,
    WebRip,
    WebDl,
    BluRay,
    Remux,
}

public enum AudioFormat
{
    Unknown,
    Mp3,
    Aac,
    Vorbis,
    Flac,
    Alac,
}
