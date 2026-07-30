using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Tests;

public class ConsoleCatalogTests
{
    [Theory]
    [InlineData("SNES", "Super Nintendo")]
    [InlineData("Super Famicom", "Super Nintendo")]
    [InlineData("Mega Drive", "Sega Genesis")]
    [InlineData("PSX", "Sony PlayStation")]
    [InlineData("GBA", "Game Boy Advance")]
    public void Resolves_an_alias_to_its_canonical_name(string alias, string expected)
        => Assert.Equal(expected, ConsoleCatalog.CanonicalName(alias));

    [Fact]
    public void Prefers_the_longest_matching_console_name()
    {
        // "Super Famicom" (alias for Super Nintendo, 13 chars) must win over "Famicom" (alias for NES, 8 chars).
        // Both match as trailing word-runs (Famicom is a suffix of Super Famicom with a space before it).
        // Without length-ordering, Famicom (added earlier in the list from NES) would match first.
        Assert.Equal("Super Nintendo", ConsoleCatalog.DetectFromQuery("Best Game Super Famicom"));
    }

    [Fact]
    public void Every_console_declares_at_least_one_extension()
    {
        Assert.All(ConsoleCatalog.Defaults, c => Assert.NotEmpty(c.Extensions));
    }

    [Fact]
    public void Every_extension_starts_with_a_dot()
    {
        // Extensions are compared against file names verbatim, so a missing dot
        // silently matches nothing.
        Assert.All(ConsoleCatalog.Defaults, c => Assert.All(c.Extensions, e => Assert.StartsWith(".", e)));
    }

    [Fact]
    public void Canonical_names_are_unique()
    {
        var names = ConsoleCatalog.Defaults.Select(c => c.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
