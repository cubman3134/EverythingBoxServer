using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Providers;

namespace EverythingBox.Server.Core.Tests;

public class SearchQueryTests
{
    [Fact]
    public void MovieIncludesYear()
        => Assert.Equal("The Matrix 1999", SearchQuery.Build(new MovieRequest { Title = "The Matrix", Year = 1999 }));

    [Fact]
    public void TvIncludesSeasonEpisode()
        => Assert.Equal("Severance S02E01", SearchQuery.Build(new TvRequest { Title = "Severance", Season = 2, Episode = 1 }));

    [Fact]
    public void MusicSearchesArtistAndAlbumNotTrack()
    {
        // The track is intentionally excluded — we search for the album, then pull
        // the track out of it during file selection.
        var query = SearchQuery.Build(new MusicRequest
        {
            Title = "Starboy",
            Artist = "The Weeknd",
            Album = "Starboy",
            Track = "I Feel It Coming",
        });

        Assert.Equal("The Weeknd Starboy", query);
        Assert.DoesNotContain("Feel", query);
    }

    [Fact]
    public void BookIncludesAuthorTitleAndFormat()
        => Assert.Equal("Brandon Sanderson Mistborn EPUB",
            SearchQuery.Build(new BookRequest { Title = "Mistborn", Author = "Brandon Sanderson", Format = "EPUB" }));

    [Fact]
    public void ComicSearchesSeriesNotIssueNumber()
        // Volume/issue feed file selection, not the query.
        => Assert.Equal("Saga", SearchQuery.Build(new ComicRequest { Title = "Saga", Issue = 3 }));

    [Fact]
    public void GeneralRequestUsesTheRawQuery()
        => Assert.Equal("blender open movie", SearchQuery.Build(new GeneralRequest { Title = "blender open movie" }));
}
