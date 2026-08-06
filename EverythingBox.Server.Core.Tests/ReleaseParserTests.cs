using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Tests;

public class ReleaseParserTests
{
    private readonly DefaultReleaseParser _parser = new();

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-GROUP", "The Matrix", 1999)]
    [InlineData("Inception (2010) 2160p UHD BluRay REMUX HEVC", "Inception", 2010)]
    [InlineData("Some Movie 720p WEB-DL", "Some Movie", null)]
    public void ParsesMovieTitleAndYear(string release, string expectedTitle, int? expectedYear)
    {
        var info = _parser.Parse(release, MediaType.Movie);

        Assert.Equal(expectedTitle, info.NormalizedTitle);
        Assert.Equal(expectedYear, info.Year);
    }

    [Theory]
    [InlineData("Movie.2020.1080p.WEB-DL.ESubs-GRP", "English")]
    [InlineData("Movie 2020 1080p WEBRip Eng.Subs", "English")]
    [InlineData("Film.2021.1080p.BluRay.VOSTFR", "French")]
    [InlineData("Filme 2019 1080p WEB Legendado", "Portuguese")]
    [InlineData("Pelicula 2018 720p Subtitulado", "Spanish")]
    public void ParsesSubtitleLanguage(string release, string expected)
        => Assert.Contains(expected, _parser.Parse(release, MediaType.Movie).SubtitleLanguages);

    [Theory]
    [InlineData("Movie.2020.1080p.WEB-DL.Multi-Subs-GRP")]
    [InlineData("Movie 2020 1080p MSubs")]
    public void ParsesMultiSubtitleMarker(string release)
        => Assert.Contains("Multi", _parser.Parse(release, MediaType.Movie).SubtitleLanguages);

    [Fact]
    public void NoSubtitlesWhenTitleHasNone()
        => Assert.Empty(_parser.Parse("The Matrix 1999 1080p BluRay x264-GRP", MediaType.Movie).SubtitleLanguages);

    [Fact]
    public void ParsesResolution()
    {
        Assert.Equal(VideoResolution.R2160p, _parser.Parse("Movie 2160p", MediaType.Movie).Resolution);
        Assert.Equal(VideoResolution.R2160p, _parser.Parse("Movie 4K UHD", MediaType.Movie).Resolution);
        Assert.Equal(VideoResolution.R1080p, _parser.Parse("Movie 1080p", MediaType.Movie).Resolution);
        Assert.Equal(VideoResolution.R720p, _parser.Parse("Movie 720p", MediaType.Movie).Resolution);
        Assert.Null(_parser.Parse("Movie with no res", MediaType.Movie).Resolution);
    }

    [Theory]
    [InlineData("Show BluRay", ReleaseSource.BluRay)]
    [InlineData("Show BluRay REMUX", ReleaseSource.Remux)]
    [InlineData("Show WEB-DL", ReleaseSource.WebDl)]
    [InlineData("Show WEBRip", ReleaseSource.WebRip)]
    [InlineData("Show HDTV", ReleaseSource.Hdtv)]
    [InlineData("Show CAM", ReleaseSource.Cam)]
    public void ParsesSource(string release, ReleaseSource expected)
        => Assert.Equal(expected, _parser.Parse(release, MediaType.Movie).Source);

    [Fact]
    public void ParsesVideoCodec()
    {
        Assert.Equal("x265", _parser.Parse("Movie HEVC", MediaType.Movie).VideoCodec);
        Assert.Equal("x265", _parser.Parse("Movie x265", MediaType.Movie).VideoCodec);
        Assert.Equal("x264", _parser.Parse("Movie h264", MediaType.Movie).VideoCodec);
        Assert.Equal("XviD", _parser.Parse("Movie XviD", MediaType.Movie).VideoCodec);
    }

    [Fact]
    public void ParsesSingleEpisode()
    {
        var info = _parser.Parse("The.Office.US.S03E07.720p.HDTV.x264-GROUP", MediaType.Tv);

        Assert.Equal("The Office US", info.NormalizedTitle);
        Assert.Equal(3, info.Season);
        Assert.Equal([7], info.Episodes);
        Assert.Equal("GROUP", info.ReleaseGroup);
    }

    [Fact]
    public void ParsesMultiEpisodeRange()
    {
        var info = _parser.Parse("Show.S01E01-E03.1080p.WEB-DL", MediaType.Tv);

        Assert.Equal(1, info.Season);
        Assert.Equal([1, 2, 3], info.Episodes);
    }

    [Fact]
    public void ParsesAltEpisodeFormat()
    {
        var info = _parser.Parse("Show 2x05 HDTV", MediaType.Tv);

        Assert.Equal(2, info.Season);
        Assert.Equal([5], info.Episodes);
    }

    [Fact]
    public void ParsesFullSeasonPack()
    {
        var info = _parser.Parse("Show.S02.1080p.BluRay", MediaType.Tv);

        Assert.Equal(2, info.Season);
        Assert.Empty(info.Episodes);
    }

    [Fact]
    public void ParsesMusicFlac()
    {
        var info = _parser.Parse("Artist - Album (2021) [FLAC]", MediaType.Music);

        Assert.Equal("Artist - Album", info.NormalizedTitle);
        Assert.Equal(2021, info.Year);
        Assert.Equal(AudioFormat.Flac, info.AudioFormat);
    }

    [Fact]
    public void ParsesMusicMp3WithBitrate()
    {
        var info = _parser.Parse("Artist - Album [MP3 320]", MediaType.Music);

        Assert.Equal(AudioFormat.Mp3, info.AudioFormat);
        Assert.Equal(320, info.AudioBitrateKbps);
    }

    [Fact]
    public void ParsesProperAndLanguage()
    {
        var info = _parser.Parse("Movie.2020.PROPER.1080p.BluRay.French.x264", MediaType.Movie);

        Assert.True(info.IsProper);
        Assert.Contains("French", info.Languages);
    }

    [Theory]
    [InlineData("Lucy Foley - The Paris Apartment Spanish EPUB")]
    [InlineData("Lucy Foley - El Apartamento de Paris (Español) EPUB")]
    [InlineData("Lucy Foley - The Paris Apartment Castellano MOBI")]
    public void DetectsSpanishBookEditions(string release)
        => Assert.Contains("Spanish", _parser.Parse(release, MediaType.Book).Languages);

    [Fact]
    public void EmptyTitleIsSafe()
    {
        var info = _parser.Parse("", MediaType.Movie);
        Assert.Null(info.Resolution);
        Assert.Null(info.Year);
    }
}
