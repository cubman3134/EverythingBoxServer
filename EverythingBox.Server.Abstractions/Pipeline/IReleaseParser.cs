namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Turns a raw release title (e.g. "The Show S01E02 1080p WEB-DL x264-GROUP")
/// into structured <see cref="ReleaseInfo"/> that the ranker can reason about.
/// </summary>
public interface IReleaseParser
{
    ReleaseInfo Parse(string releaseTitle, MediaType mediaType);
}
