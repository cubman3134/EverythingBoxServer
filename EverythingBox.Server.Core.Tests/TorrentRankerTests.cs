using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Parsing;
using EverythingBox.Server.Core.Ranking;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class TorrentRankerTests
{
    private readonly DefaultReleaseParser _parser = new();
    private readonly DefaultTorrentRanker _ranker = new();

    /// <summary>Build a result and attach parsed info, the way the grabber does.</summary>
    private TorrentResult Make(string title, MediaType type, int seeders = 10, long? size = null)
        => new()
        {
            Title = title,
            ProviderName = "test",
            MagnetUri = new Uri("magnet:?xt=urn:btih:abc"),
            Seeders = seeders,
            SizeBytes = size,
            ParsedInfo = _parser.Parse(title, type),
        };

    [Fact]
    public void RejectsResultsWithoutDownloadLink()
    {
        var noLink = Make("The Matrix 1999 1080p BluRay", MediaType.Movie) with { MagnetUri = null };
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [noLink], RankingOptions.Default);

        Assert.Empty(ranked);
    }

    [Fact]
    public void RejectsBelowMinSeeders()
    {
        var result = Make("The Matrix 1999 1080p BluRay", MediaType.Movie, seeders: 0);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [result], new RankingOptions { MinSeeders = 1 });

        Assert.Empty(ranked);
    }

    [Fact]
    public void RejectsIrrelevantTitle()
    {
        var wrong = Make("Completely Different Film 1080p BluRay", MediaType.Movie);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [wrong], RankingOptions.Default);

        Assert.Empty(ranked);
    }

    [Fact]
    public void RejectsWrongMovieYear()
    {
        var remake = Make("The Matrix 2099 1080p BluRay", MediaType.Movie);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix", Year = 1999 }, [remake], RankingOptions.Default);

        Assert.Empty(ranked);
    }

    [Fact]
    public void RejectsWrongTvEpisode()
    {
        var wrongEp = Make("The Office US S03E10 1080p WEB-DL", MediaType.Tv);
        var ranked = _ranker.Rank(
            new TvRequest { Title = "The Office US", Season = 3, Episode = 7 },
            [wrongEp], RankingOptions.Default);

        Assert.Empty(ranked);
    }

    [Fact]
    public void PrefersHigherQualitySourceByDefault()
    {
        var cam = Make("The Matrix 1999 CAM", MediaType.Movie, seeders: 500);
        var bluray = Make("The Matrix 1999 1080p BluRay", MediaType.Movie, seeders: 10);

        var best = _ranker.SelectBest(
            new MovieRequest { Title = "The Matrix" }, [cam, bluray], RankingOptions.Default);

        // BluRay wins despite far fewer seeders — quality outweighs the tiebreak.
        Assert.Equal(bluray.Title, best!.Title);
    }

    [Fact]
    public void HonorsPreferredResolution()
    {
        var hd = Make("The Matrix 1999 1080p BluRay", MediaType.Movie, seeders: 10);
        var uhd = Make("The Matrix 1999 2160p BluRay", MediaType.Movie, seeders: 10);

        var options = new RankingOptions
        {
            PreferredResolutions = [VideoResolution.R1080p, VideoResolution.R2160p],
        };

        var best = _ranker.SelectBest(new MovieRequest { Title = "The Matrix" }, [uhd, hd], options);
        Assert.Equal(hd.Title, best!.Title);
    }

    [Fact]
    public void PrefersReleaseWithPreferredSubtitleLanguage()
    {
        var noSubs = Make("The Matrix 1999 1080p BluRay x264-A", MediaType.Movie, seeders: 50);
        var engSubs = Make("The Matrix 1999 1080p BluRay x264 ESubs-B", MediaType.Movie, seeders: 10);

        var options = new RankingOptions { PreferredSubtitleLanguages = ["English"] };
        var best = _ranker.SelectBest(new MovieRequest { Title = "The Matrix" }, [noSubs, engSubs], options);

        // The English-subbed release wins despite far fewer seeders.
        Assert.Equal(engSubs.Title, best!.Title);
    }

    [Fact]
    public void SubtitlePreferenceIgnoredWithoutAMatch()
    {
        // No subtitle preference set: the higher-seeded release wins as usual.
        var noSubs = Make("The Matrix 1999 1080p BluRay x264-A", MediaType.Movie, seeders: 5000);
        var engSubs = Make("The Matrix 1999 1080p BluRay x264 ESubs-B", MediaType.Movie, seeders: 10);

        var best = _ranker.SelectBest(new MovieRequest { Title = "The Matrix" }, [noSubs, engSubs], RankingOptions.Default);
        Assert.Equal(noSubs.Title, best!.Title);
    }

    [Fact]
    public void SeedersBreakTiesAtEqualQuality()
    {
        var low = Make("The Matrix 1999 1080p BluRay x264-A", MediaType.Movie, seeders: 5);
        var high = Make("The Matrix 1999 1080p BluRay x264-B", MediaType.Movie, seeders: 5000);

        var best = _ranker.SelectBest(new MovieRequest { Title = "The Matrix" }, [low, high], RankingOptions.Default);
        Assert.Equal(high.Title, best!.Title);
    }

    [Fact]
    public void MusicMatchesAlbumWhenRequestingASpecificSong()
    {
        // Requesting "I Feel It Coming" from the Starboy album must still match the
        // album release (the song isn't in the album's release name).
        var album = Make("The Weeknd - Starboy (2016) [FLAC]", MediaType.Music, seeders: 30);

        var ranked = _ranker.Rank(
            new MusicRequest { Title = "Starboy", Album = "Starboy", Artist = "The Weeknd", Track = "I Feel It Coming" },
            [album],
            RankingOptions.Default);

        Assert.Single(ranked);
    }

    [Fact]
    public void PrefersFlacForMusicByDefault()
    {
        var mp3 = Make("Artist - Album [MP3 320]", MediaType.Music, seeders: 100);
        var flac = Make("Artist - Album [FLAC]", MediaType.Music, seeders: 10);

        var best = _ranker.SelectBest(
            new MusicRequest { Title = "Artist Album" }, [mp3, flac], RankingOptions.Default);

        Assert.Equal(flac.Title, best!.Title);
    }

    [Fact]
    public void AudiobookSearchExcludesEbooksButKeepsAudio()
    {
        var ebook = Make("Project Hail Mary by Andy Weir EPUB", MediaType.Audiobook);
        var audiobook = Make("Andy Weir - Project Hail Mary Audiobook M4B", MediaType.Audiobook);
        var plainAudio = Make("Andy Weir - Project Hail Mary", MediaType.Audiobook);

        var ranked = _ranker.Rank(
            new AudiobookRequest { Title = "Project Hail Mary" },
            [ebook, audiobook, plainAudio], RankingOptions.Default);

        Assert.DoesNotContain(ranked, s => s.Result.Title.Contains("EPUB"));
        Assert.Contains(ranked, s => s.Result.Title.Contains("M4B"));
        Assert.Contains(ranked, s => s.Result == plainAudio);
    }

    [Fact]
    public void MusicSearchExcludesVideoAndAudiobooks()
    {
        var album = Make("Daft Punk - Random Access Memories [FLAC]", MediaType.Music);
        var movie = Make("Random Access Memories 2024 1080p WEBRip x264", MediaType.Music);

        var ranked = _ranker.Rank(
            new MusicRequest { Title = "Random Access Memories", Artist = "Daft Punk" },
            [album, movie], RankingOptions.Default);

        var only = Assert.Single(ranked);
        Assert.Equal(album, only.Result);
    }

    [Fact]
    public void BookSearchExcludesAudiobookAndVideo()
    {
        var epub = Make("Frank Herbert - Dune EPUB", MediaType.Book);
        var audiobook = Make("Frank Herbert - Dune Audiobook M4B", MediaType.Book);
        var movie = Make("Dune 1984 1080p BluRay x264", MediaType.Book);

        var ranked = _ranker.Rank(
            new BookRequest { Title = "Dune" }, [epub, audiobook, movie], RankingOptions.Default);

        var only = Assert.Single(ranked);
        Assert.Equal(epub, only.Result);
    }

    [Fact]
    public void BookSearchExcludesComics()
    {
        var prose = Make("Frank Herbert - Dune EPUB", MediaType.Book);
        var comic = Make("Dune The Graphic Novel CBZ", MediaType.Book);

        var ranked = _ranker.Rank(new BookRequest { Title = "Dune" }, [prose, comic], RankingOptions.Default);

        var only = Assert.Single(ranked);
        Assert.Equal(prose, only.Result);
    }

    [Fact]
    public void BookSearchPrefersEnglishOverForeignEdition()
    {
        // The English edition is untagged (the common case); the Spanish translation advertises itself and
        // has far more seeders. English must still win - a foreign edition is a different book.
        var english = Make("Lucy Foley - The Paris Apartment EPUB", MediaType.Book, seeders: 5);
        var spanish = Make("Lucy Foley - The Paris Apartment Spanish EPUB", MediaType.Book, seeders: 500);
        var options = new RankingOptions { PreferredLanguages = ["English"] };

        var best = _ranker.SelectBest(
            new BookRequest { Title = "The Paris Apartment" }, [spanish, english], options);

        Assert.Equal(english, best);
    }

    [Fact]
    public void BookSearchStillReturnsForeignEditionWhenItIsTheOnlyOption()
    {
        // Penalised, not filtered: if the only copy is foreign, it's still better than nothing.
        var spanish = Make("Lucy Foley - The Paris Apartment Spanish EPUB", MediaType.Book);
        var options = new RankingOptions { PreferredLanguages = ["English"] };

        var best = _ranker.SelectBest(
            new BookRequest { Title = "The Paris Apartment" }, [spanish], options);

        Assert.Equal(spanish, best);
    }

    [Fact]
    public void ComicSearchExcludesVideoAndAudiobooksButKeepsComics()
    {
        var comic = Make("Saga 001-054 (2012-2022) CBZ", MediaType.Comic);
        var movie = Make("Saga 2024 1080p WEBRip x264", MediaType.Comic);
        var audiobook = Make("Saga Audiobook M4B", MediaType.Comic);

        var ranked = _ranker.Rank(new ComicRequest { Title = "Saga" }, [comic, movie, audiobook], RankingOptions.Default);

        var only = Assert.Single(ranked);
        Assert.Equal(comic, only.Result);
    }

    [Fact]
    public void MovieSearchExcludesEbooks()
    {
        var movie = Make("Dune 1984 1080p BluRay x264", MediaType.Movie);
        var ebook = Make("Dune by Frank Herbert EPUB", MediaType.Movie);

        var ranked = _ranker.Rank(
            new MovieRequest { Title = "Dune", Year = 1984 }, [movie, ebook], RankingOptions.Default);

        var only = Assert.Single(ranked);
        Assert.Equal(movie, only.Result);
    }

    [Fact]
    public void BannedTermDisqualifies()
    {
        var result = Make("The Matrix 1999 1080p BluRay", MediaType.Movie);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" },
            [result],
            new RankingOptions { BannedTerms = ["BluRay"] });

        Assert.Empty(ranked);
    }
}
