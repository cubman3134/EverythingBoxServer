using EverythingBox.Server.Abstractions;
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
    public void PerRequestPreferredLanguageBoostsMatchingReleaseOverAnEqualOne()
    {
        var english = Make("The Matrix 1999 English 1080p BluRay x264-A", MediaType.Movie, seeders: 50);
        var spanish = Make("The Matrix 1999 Spanish 1080p BluRay x264-B", MediaType.Movie, seeders: 10);

        // Caller prefers Spanish -> the Spanish release wins despite fewer seeders.
        var best = _ranker.SelectBest(
            new MovieRequest { Title = "The Matrix", PreferredLanguage = "Spanish" },
            [english, spanish], RankingOptions.Default);
        Assert.Equal(spanish.Title, best!.Title);

        // No per-request preference -> today's ranking (higher-seeded English) wins, unchanged.
        var noPref = _ranker.SelectBest(
            new MovieRequest { Title = "The Matrix" }, [english, spanish], RankingOptions.Default);
        Assert.Equal(english.Title, noPref!.Title);
    }

    [Fact]
    public void PerRequestPreferredLanguageBoostsMatchingSubtitleRelease()
    {
        var noSubs = Make("The Matrix 1999 1080p BluRay x264-A", MediaType.Movie, seeders: 50);
        var engSubs = Make("The Matrix 1999 1080p BluRay x264 ESubs-B", MediaType.Movie, seeders: 10);

        // The per-request language also feeds the SUBTITLE fold (not just audio): with no config, a
        // release advertising subtitles in the caller's language wins over an equal one without.
        var best = _ranker.SelectBest(
            new MovieRequest { Title = "The Matrix", PreferredLanguage = "English" },
            [noSubs, engSubs], RankingOptions.Default);
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

    // --- a web rip is a film, except where a web tag means something else --------------
    // Every release title below is verbatim from a live indexer.

    [Fact]
    public void AudiobookSearchExcludesAWebRipCarryingNoOtherVideoMarker()
    {
        // The film rip has no resolution and no codec in its name, so the rip tag is the only
        // video signal there is - and it out-seeds every real audiobook of the same book.
        var filmRip = Make("Project Hail Mary [2026] WEBrip YG", MediaType.Audiobook, seeders: 281);
        var audiobook = Make("Andy Weir - Project Hail Mary", MediaType.Audiobook, seeders: 222);

        var ranked = _ranker.Rank(
            new AudiobookRequest { Title = "Project Hail Mary" }, [filmRip, audiobook], RankingOptions.Default);

        var only = Assert.Single(ranked);
        Assert.Equal(audiobook, only.Result);
    }

    [Fact]
    public void BookSearchExcludesAWebRipCarryingNoOtherVideoMarker()
    {
        var filmRip = Make("Project Hail Mary [2026] WEBrip YG", MediaType.Book, seeders: 281);
        var ebook = Make("Project Hail Mary by Andy Weir EPUB", MediaType.Book, seeders: 271);

        var ranked = _ranker.Rank(
            new BookRequest { Title = "Project Hail Mary" }, [filmRip, ebook], RankingOptions.Default);

        Assert.DoesNotContain(ranked, s => s.Result == filmRip);
        Assert.Contains(ranked, s => s.Result == ebook);
    }

    [Fact]
    public void AWorkWhoseOwnTitleContainsTheWordWebStillAnswersABookOrAudiobookSearch()
    {
        // The parser reaches ReleaseSource.WebDl from the bare word "web", so these two both look
        // web-sourced. Neither is a rip, and both are exactly what was asked for.
        var narrated = Make("E B White - Charlotte's Web - Meryl Streep", MediaType.Audiobook);
        var ebook = Make("Charlotte's Web (Full Color) - E. B. White [Epub & PDF]", MediaType.Book);

        var audiobooks = _ranker.Rank(
            new AudiobookRequest { Title = "Charlotte's Web" }, [narrated], RankingOptions.Default);
        var books = _ranker.Rank(
            new BookRequest { Title = "Charlotte's Web" }, [ebook], RankingOptions.Default);

        Assert.Equal(narrated, Assert.Single(audiobooks).Result);
        Assert.Equal(ebook, Assert.Single(books).Result);
    }

    [Fact]
    public void AnAudiobookOrEbookTaggedAsAWebRipSurvivesOnItsOwnMarker()
    {
        // The rip tag is a *conflicting* marker, so a release that also carries its own definite
        // marker keeps the benefit of the doubt rather than being dropped by provenance alone.
        var audiobook = Make("Andy Weir - Project Hail Mary (Unabridged) WEB-DL M4B", MediaType.Audiobook);
        var ebook = Make("Andy Weir - Project Hail Mary WEBRip EPUB", MediaType.Book);

        var audiobooks = _ranker.Rank(
            new AudiobookRequest { Title = "Project Hail Mary" }, [audiobook], RankingOptions.Default);
        var books = _ranker.Rank(
            new BookRequest { Title = "Project Hail Mary" }, [ebook], RankingOptions.Default);

        Assert.Equal(audiobook, Assert.Single(audiobooks).Result);
        Assert.Equal(ebook, Assert.Single(books).Result);
    }

    [Fact]
    public void MusicSearchStillAcceptsAWebSourcedAlbum()
    {
        // A music release tagged WEB was bought from a store, not ripped from a stream. This is the
        // case the global web exclusion exists for, and it is unchanged.
        var bareWeb = Make("Charlotte Sands - can we start over - 2024 - WEB FLAC 16BITS 44.1KHZ-EICHBAUM",
            MediaType.Music);
        var hyphenated = Make("Kendrick Lamar - untitled unmastered. {WEB-FLAC} (2016)", MediaType.Music);

        Assert.Single(_ranker.Rank(
            new MusicRequest { Title = "can we start over", Artist = "Charlotte Sands" },
            [bareWeb], RankingOptions.Default));
        Assert.Single(_ranker.Rank(
            new MusicRequest { Title = "untitled unmastered", Artist = "Kendrick Lamar" },
            [hyphenated], RankingOptions.Default));
    }

    [Fact]
    public void ComicSearchStillAcceptsAScanTaggedAsAWebRip()
    {
        // Scanned comics are themselves tagged "(webrip)" - the tag means the scan came off a
        // digital storefront. Reading it as video here would empty the shelf.
        var scan = Make("Friends of Spirou (Europe Comics 2023) (webrip) (MagicMan-DCP).cbr", MediaType.Comic);

        // The same, in the very common form that names no file format at all, so nothing but the
        // request type is left to keep it. Title and scan-group convention are both real; the two
        // are combined here because the reachable indexers carry few comics.
        var unlabelled = Make("Absolute Green Lantern 013 (2026) (Digital) (webrip) (Pyrate-DCP)", MediaType.Comic);

        var ranked = _ranker.Rank(
            new ComicRequest { Title = "Friends of Spirou" }, [scan], RankingOptions.Default);
        var unlabelledRanked = _ranker.Rank(
            new ComicRequest { Title = "Absolute Green Lantern" }, [unlabelled], RankingOptions.Default);

        Assert.Equal(scan, Assert.Single(ranked).Result);
        Assert.Equal(unlabelled, Assert.Single(unlabelledRanked).Result);
    }

    [Fact]
    public void MovieSearchStillRejectsASoundtrackTaggedAsAWebRelease()
    {
        // Movie/Tv read "video" the other way round - as an excuse for an audio-format tag - so the
        // rip rule must not reach them, or an album would start answering a film search.
        var soundtrack = Make("Dune Original Motion Picture Soundtrack WEB-DL FLAC", MediaType.Movie);

        var ranked = _ranker.Rank(new MovieRequest { Title = "Dune" }, [soundtrack], RankingOptions.Default);

        Assert.Empty(ranked);
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

    private TorrentResult MatrixWithGroup(string group) =>
        Make("The Matrix 1999", MediaType.Movie) with
        { ParsedInfo = new ReleaseInfo { ReleaseGroup = group } };

    [Fact]
    public void PrefersHigherPriorityReleaseGroup()
    {
        var a = MatrixWithGroup("GroupA");
        var b = MatrixWithGroup("GroupB");
        var opts = new RankingOptions { PreferredReleaseGroups = ["GroupA", "GroupB"] };

        // Input order b,a — result order must come from the preference, not the input.
        var ranked = _ranker.Rank(new MovieRequest { Title = "The Matrix" }, [b, a], opts);

        Assert.Equal("GroupA", ranked[0].Result.ParsedInfo!.ReleaseGroup);
    }

    [Fact]
    public void ReversingThePreferenceReversesTheOrder()
    {
        var a = MatrixWithGroup("GroupA");
        var b = MatrixWithGroup("GroupB");
        var opts = new RankingOptions { PreferredReleaseGroups = ["GroupB", "GroupA"] };

        var ranked = _ranker.Rank(new MovieRequest { Title = "The Matrix" }, [a, b], opts);

        Assert.Equal("GroupB", ranked[0].Result.ParsedInfo!.ReleaseGroup);
    }

    [Fact]
    public void ReleaseGroupMatchIsCaseInsensitive()
    {
        var hit = MatrixWithGroup("GroupA");
        var miss = MatrixWithGroup("GroupB");
        var opts = new RankingOptions { PreferredReleaseGroups = ["groupa"] };

        var ranked = _ranker.Rank(new MovieRequest { Title = "The Matrix" }, [miss, hit], opts);

        Assert.Equal("GroupA", ranked[0].Result.ParsedInfo!.ReleaseGroup);
    }

    [Fact]
    public void UnlistedReleaseGroupIsNotPenalised()
    {
        // An unlisted group must score identically to no-preference — the term only ADDS.
        var r = MatrixWithGroup("GroupZ");
        var withPref = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [r],
            new RankingOptions { PreferredReleaseGroups = ["GroupA"] });
        var noPref = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [r], RankingOptions.Default);

        Assert.Equal(noPref[0].Score, withPref[0].Score);
    }

    [Fact]
    public void AbsentReleaseGroupIsNotPenalised()
    {
        var r = Make("The Matrix 1999", MediaType.Movie) with
        { ParsedInfo = new ReleaseInfo { ReleaseGroup = null } };
        var withPref = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [r],
            new RankingOptions { PreferredReleaseGroups = ["GroupA"] });
        var noPref = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [r], RankingOptions.Default);

        Assert.Equal(noPref[0].Score, withPref[0].Score);
    }

    [Fact]
    public void EmptyPreferredReleaseGroupsRanksNormally()
    {
        var r = MatrixWithGroup("GroupA");
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix" }, [r], RankingOptions.Default);

        Assert.Single(ranked); // default list is empty → term contributes 0, result still ranks
    }

    [Fact]
    public void ReleaseMatchingOnlyAnAlternateTitleIsEligible()
    {
        var alt = Make("Localised Name 1080p BluRay", MediaType.Movie);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix", AlternateTitles = ["Localised Name"] },
            [alt], RankingOptions.Default);

        Assert.Single(ranked);
    }

    [Fact]
    public void ReleaseMatchingNeitherPrimaryNorAlternateIsRejected()
    {
        var wrong = Make("Completely Different Film 1080p BluRay", MediaType.Movie);
        var request = new MovieRequest { Title = "The Matrix", AlternateTitles = ["Localised Name"] };

        // Rejected: it never ranks...
        Assert.Empty(_ranker.Rank(request, [wrong], RankingOptions.Default));
        // ...and the relevance gate reports the primary subject's missing terms.
        var (relevant, why) = InvokeIsRelevant(request, wrong);
        Assert.False(relevant);
        Assert.Contains("title missing terms", why);
    }

    [Fact]
    public void PrimaryStillMatchesWhenAlternatesDoNot()
    {
        var byPrimary = Make("The Matrix 1080p BluRay", MediaType.Movie);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix", AlternateTitles = ["Localised Name"] },
            [byPrimary], RankingOptions.Default);

        Assert.Single(ranked);
    }

    [Fact]
    public void MusicAlbumPrimaryAcceptsAlternateAlbumTitledRelease()
    {
        var altAlbum = Make("Alt Album FLAC", MediaType.Music);
        var ranked = _ranker.Rank(
            new MusicRequest { Title = "song", Album = "The Album", AlternateTitles = ["Alt Album"] },
            [altAlbum], RankingOptions.Default);

        Assert.Single(ranked);
    }

    [Fact]
    public void AlternateTitleDoesNotLoosenSeasonCheck()
    {
        // Alternate matches the title, but the release is season 1 for a season-2 request.
        var season1 = Make("Localised Name S01E01 1080p WEB-DL", MediaType.Tv);
        var ranked = _ranker.Rank(
            new TvRequest { Title = "The Show", Season = 2, AlternateTitles = ["Localised Name"] },
            [season1], RankingOptions.Default);

        Assert.Empty(ranked);
    }

    [Fact]
    public void AlternateTitleDoesNotLoosenYearCheck()
    {
        // Alternate matches the title, but the release year is 2001 for a 1999 request.
        var wrongYear = Make("Localised Name 2001 1080p BluRay", MediaType.Movie);
        var ranked = _ranker.Rank(
            new MovieRequest { Title = "The Matrix", Year = 1999, AlternateTitles = ["Localised Name"] },
            [wrongYear], RankingOptions.Default);

        Assert.Empty(ranked);
    }

    [Fact]
    public void NonLatinAlternateDoesNotWildcardTheGate()
    {
        // The CJK alternate tokenizes to the empty set; empty ⊆ anything must NOT match every
        // release. An unrelated Latin release is rejected...
        var request = new MovieRequest { Title = "Spirited Away", AlternateTitles = ["千と千尋の神隠し"] };
        var unrelated = Make("Some Other Movie 1080p", MediaType.Movie);
        Assert.Empty(_ranker.Rank(request, [unrelated], RankingOptions.Default));

        // ...while a release that actually matches the (Latin) primary is still accepted.
        var match = Make("Spirited Away 1080p", MediaType.Movie);
        Assert.Single(_ranker.Rank(request, [match], RankingOptions.Default));
    }

    [Fact]
    public void PurelyNonLatinRequestAcceptsWhenTitleCannotBeAssessed()
    {
        // Primary AND every alternate are non-Latin, so nothing is tokenizable and title relevance
        // can't be assessed. The gate must fall back to accept, not reject a legitimate search.
        var request = new MovieRequest { Title = "千と千尋の神隠し", AlternateTitles = ["となりのトトロ"] };
        var release = Make("Some Release 1080p", MediaType.Movie);

        Assert.Single(_ranker.Rank(request, [release], RankingOptions.Default));
    }

    // Rank() surfaces only eligibility, not the rejection reason, so read the private
    // IsRelevant(request, result, out why) directly to assert the message shape.
    private (bool Relevant, string Why) InvokeIsRelevant(MediaRequest request, TorrentResult r)
    {
        var method = typeof(DefaultTorrentRanker).GetMethod(
            "IsRelevant",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var args = new object?[] { request, r, null };
        var relevant = (bool)method.Invoke(null, args)!;
        return (relevant, (string)args[2]!);
    }
}
