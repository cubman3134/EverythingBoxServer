namespace EverythingBox.Server.LocalLibrary;

/// <summary>The cached result of the expensive per-item parse: the raw .nfo fields and the located
/// poster path. Callers format these into title/subtitle/panel, so one entry serves both the
/// catalog scan and the meta panel.</summary>
internal sealed record ItemMeta(string? NfoTitle, int? Year, string? Plot, string? PosterPath);
