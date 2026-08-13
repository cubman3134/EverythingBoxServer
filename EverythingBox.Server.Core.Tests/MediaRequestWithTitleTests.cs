using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Tests;

/// <summary>WithTitle returns the same concrete subtype with a swapped primary Title and
/// every other field — base and subtype-specific — copied through unchanged.</summary>
public class MediaRequestWithTitleTests
{
    [Fact]
    public void MovieRequest_swaps_title_and_preserves_edition_and_year()
    {
        var original = new MovieRequest
        {
            Title = "Blade Runner",
            Year = 1982,
            ExternalIds = new Dictionary<string, string> { ["imdb"] = "tt0083658" },
            AdditionalTerms = ["remastered"],
            PreferredLanguage = "Spanish",
            Edition = "Final Cut",
        };

        var copy = Assert.IsType<MovieRequest>(original.WithTitle("X"));
        Assert.Equal("X", copy.Title);
        Assert.Equal(original.Year, copy.Year);
        Assert.Same(original.ExternalIds, copy.ExternalIds);
        Assert.Same(original.AdditionalTerms, copy.AdditionalTerms);
        Assert.Same(original.AlternateTitles, copy.AlternateTitles);
        Assert.Equal("Spanish", copy.PreferredLanguage);
        Assert.Equal(original.Edition, copy.Edition);
    }

    [Fact]
    public void TvRequest_swaps_title_and_preserves_season_and_episode()
    {
        var original = new TvRequest
        {
            Title = "The Wire",
            Year = 2002,
            Season = 3,
            Episode = 7,
            AbsoluteEpisode = 30,
            FullSeason = true,
            PreferredLanguage = "Spanish",
        };

        var copy = Assert.IsType<TvRequest>(original.WithTitle("X"));
        Assert.Equal("X", copy.Title);
        Assert.Equal(original.Year, copy.Year);
        Assert.Equal(original.Season, copy.Season);
        Assert.Equal(original.Episode, copy.Episode);
        Assert.Equal(original.AbsoluteEpisode, copy.AbsoluteEpisode);
        Assert.Equal(original.FullSeason, copy.FullSeason);
        Assert.Equal("Spanish", copy.PreferredLanguage);
    }

    [Fact]
    public void MusicRequest_swaps_title_and_preserves_album()
    {
        var original = new MusicRequest
        {
            Title = "Kind of Blue",
            Artist = "Miles Davis",
            Album = "Kind of Blue",
            Track = "So What",
        };

        var copy = Assert.IsType<MusicRequest>(original.WithTitle("X"));
        Assert.Equal("X", copy.Title);
        Assert.Equal(original.Artist, copy.Artist);
        Assert.Equal(original.Album, copy.Album);
        Assert.Equal(original.Track, copy.Track);
    }

    [Fact]
    public void GeneralRequest_swaps_title_and_preserves_kind_and_filetypes()
    {
        var original = new GeneralRequest
        {
            Title = "Chrono Trigger",
            Kind = MediaType.PcGame,
            FileType = "sfc",
            FileTypes = [".sfc", ".smc"],
            FileFilters = ["usa"],
        };

        var copy = Assert.IsType<GeneralRequest>(original.WithTitle("X"));
        Assert.Equal("X", copy.Title);
        Assert.Equal(original.Kind, copy.Kind);
        Assert.Equal(original.FileType, copy.FileType);
        Assert.Same(original.FileTypes, copy.FileTypes);
        Assert.Same(original.FileFilters, copy.FileFilters);
    }

    [Fact]
    public void AlternateTitles_are_carried_through()
    {
        var original = new MovieRequest
        {
            Title = "The Seven Samurai",
            AlternateTitles = ["Shichinin no Samurai", "Los siete samuráis"],
        };

        var copy = Assert.IsType<MovieRequest>(original.WithTitle("X"));
        Assert.Equal("X", copy.Title);
        Assert.Same(original.AlternateTitles, copy.AlternateTitles);
        Assert.Equal(original.AlternateTitles, copy.AlternateTitles);
    }
}
