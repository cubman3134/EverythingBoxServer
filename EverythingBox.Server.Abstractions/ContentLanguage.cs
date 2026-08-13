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
