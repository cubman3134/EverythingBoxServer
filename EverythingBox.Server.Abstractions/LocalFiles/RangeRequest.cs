using System.Globalization;

namespace EverythingBox.Server.Abstractions;

public enum RangeKind { Full, Partial, Unsatisfiable }

public readonly record struct RangeResult(RangeKind Kind, long Start, long Length)
{
    public static readonly RangeResult Full = new(RangeKind.Full, 0, 0);
    public static readonly RangeResult Unsatisfiable = new(RangeKind.Unsatisfiable, 0, 0);
    public static RangeResult Partial(long start, long length) => new(RangeKind.Partial, start, length);
}

/// <summary>
/// Parses a single HTTP byte-range header against a known total length. Anything it does not
/// understand (no header, wrong unit, multiple ranges, garbage, or a start after the end) degrades
/// to <see cref="RangeKind.Full"/> — serve the whole file (200) — rather than erroring.
/// </summary>
public static class RangeRequest
{
    public static RangeResult Parse(string? header, long totalLength)
    {
        if (totalLength <= 0 || string.IsNullOrWhiteSpace(header))
            return RangeResult.Full;

        var trimmed = header.Trim();
        const string prefix = "bytes=";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return RangeResult.Full;

        var spec = trimmed[prefix.Length..];
        if (spec.Contains(','))            // multi-range: not worth it for media → serve whole
            return RangeResult.Full;

        var dash = spec.IndexOf('-');
        if (dash < 0)
            return RangeResult.Full;

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];

        // Suffix form "-N": the last N bytes.
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                return RangeResult.Full;
            var start = Math.Max(0, totalLength - suffix);
            return RangeResult.Partial(start, totalLength - start);
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var from) || from < 0)
            return RangeResult.Full;

        if (from >= totalLength)
            return RangeResult.Unsatisfiable;

        // Open-ended "start-": to EOF.
        if (endText.Length == 0)
            return RangeResult.Partial(from, totalLength - from);

        // Closed "start-end".
        if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var to) || to < 0)
            return RangeResult.Full;
        if (to < from)
            return RangeResult.Full;

        var last = Math.Min(to, totalLength - 1);
        return RangeResult.Partial(from, last - from + 1);
    }
}
