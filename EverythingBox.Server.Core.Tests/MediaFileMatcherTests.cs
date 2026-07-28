using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Selection;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class MediaFileMatcherTests
{
    private static DebridLink File(string name, long size = 1000)
        => new(name, new Uri($"http://dl/{Uri.EscapeDataString(name)}"), size);

    [Fact]
    public void SelectsSingleEpisodeFromSeasonPack()
    {
        var files = new[]
        {
            File("The.Office.US.S03E01.1080p.WEB-DL.mkv", 1_000_000),
            File("The.Office.US.S03E02.1080p.WEB-DL.mkv", 1_100_000),
            File("The.Office.US.S03E03.1080p.WEB-DL.mkv", 1_050_000),
            File("season.nfo", 500),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new TvRequest { Title = "The Office US", Season = 3, Episode = 2 }, files);

        var only = Assert.Single(selected);
        Assert.Contains("S03E02", only.FileName);
    }

    [Fact]
    public void ExcludesSampleFile()
    {
        var files = new[]
        {
            File("Show.S01E05.1080p.mkv", 2_000_000),
            File("Show.S01E05.sample.mkv", 5_000),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new TvRequest { Title = "Show", Season = 1, Episode = 5 }, files);

        var only = Assert.Single(selected);
        Assert.DoesNotContain("sample", only.FileName.ToLowerInvariant());
    }

    [Fact]
    public void SpecificEpisodeNotInPackReturnsNothing()
    {
        var files = new[]
        {
            File("Show.S01E01.mkv"),
            File("Show.S01E02.mkv"),
        };

        // Requesting season 2 from a season-1 pack matches nothing. A specific
        // episode was asked for, so download nothing rather than the whole pack.
        var selected = MediaFileMatcher.SelectForRequest(
            new TvRequest { Title = "Show", Season = 2, Episode = 1 }, files);

        Assert.Empty(selected);
    }

    [Fact]
    public void SelectsTrackByNumber()
    {
        var files = new[]
        {
            File("01 - Intro.flac"),
            File("02 - The Hit.flac"),
            File("03 - Outro.flac"),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new MusicRequest { Title = "Album", Track = "2" }, files);

        var only = Assert.Single(selected);
        Assert.Contains("The Hit", only.FileName);
    }

    [Fact]
    public void SelectsTrackByTitle()
    {
        var files = new[]
        {
            File("Dolly Parton - 04 - Coat of Many Colors.flac"),
            File("Dolly Parton - 05 - Jolene.flac"),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new MusicRequest { Title = "Jolene", Artist = "Dolly Parton", Track = "Jolene" }, files);

        var only = Assert.Single(selected);
        Assert.Contains("Jolene", only.FileName);
    }

    [Fact]
    public void SelectsSingleBookFromBundle()
    {
        var files = new[]
        {
            File("Frank Herbert - Dune.epub", 2_000_000),
            File("Frank Herbert - Dune Messiah.epub", 1_800_000),
            File("Frank Herbert - Children of Dune.epub", 1_900_000),
            File("cover.jpg", 50_000),
        };

        var selected = MediaFileMatcher.SelectForRequest(new BookRequest { Title = "Dune" }, files);

        var only = Assert.Single(selected);
        Assert.Equal("Frank Herbert - Dune.epub", only.FileName); // not "Dune Messiah"
    }

    [Fact]
    public void DistinguishesSequelFromBaseBook()
    {
        var files = new[]
        {
            File("Frank Herbert - Dune.epub"),
            File("Frank Herbert - Dune Messiah.epub"),
        };

        var selected = MediaFileMatcher.SelectForRequest(new BookRequest { Title = "Dune Messiah" }, files);

        var only = Assert.Single(selected);
        Assert.Contains("Messiah", only.FileName);
    }

    [Fact]
    public void PrefersRequestedBookFormat()
    {
        var files = new[]
        {
            File("Mistborn.epub"),
            File("Mistborn.pdf"),
            File("Mistborn.mobi"),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new BookRequest { Title = "Mistborn", Format = "pdf" }, files);

        var only = Assert.Single(selected);
        Assert.EndsWith(".pdf", only.FileName);
    }

    [Fact]
    public void BundleTitleWithNoSingleMatchReturnsAll()
    {
        var files = new[]
        {
            File("Frank Herbert - Dune.epub"),
            File("Frank Herbert - Dune Messiah.epub"),
        };

        // No single file matches "saga complete" -> keep the whole bundle.
        var selected = MediaFileMatcher.SelectForRequest(new BookRequest { Title = "Dune Saga Complete" }, files);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectsSpecificComicIssueFromSeries()
    {
        var files = new[]
        {
            File("Saga 001 (2012).cbz"),
            File("Saga 002 (2012).cbz"),
            File("Saga 003 (2012).cbz"),
        };

        var selected = MediaFileMatcher.SelectForRequest(new ComicRequest { Title = "Saga", Issue = 2 }, files);

        var only = Assert.Single(selected);
        Assert.Contains("002", only.FileName);
    }

    [Fact]
    public void SelectsSpecificMangaVolume()
    {
        var files = new[]
        {
            File("Berserk v01.cbz"),
            File("Berserk v02.cbz"),
            File("Berserk v03.cbz"),
        };

        var selected = MediaFileMatcher.SelectForRequest(new ComicRequest { Title = "Berserk", Volume = 3 }, files);

        var only = Assert.Single(selected);
        Assert.Contains("v03", only.FileName);
    }

    [Fact]
    public void ChapterRequestResolvesToContainingVolume()
    {
        // "Mato Seihei no Slave" vol 22 / chapter 181: the pack is split per volume,
        // so there is no chapter-181 file — chapter 181 lives inside volume 22.
        var files = new[]
        {
            File("Mato Seihei no Slave v20.cbz"),
            File("Mato Seihei no Slave v21.cbz"),
            File("Mato Seihei no Slave v22.cbz"),
            File("Mato Seihei no Slave v23.cbz"),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new ComicRequest { Title = "Mato Seihei no Slave", Volume = 22, Chapter = 181 }, files);

        var only = Assert.Single(selected);
        Assert.Contains("v22", only.FileName);
    }

    [Fact]
    public void ChapterRequestPrefersChapterFileWhenSplitByChapter()
    {
        // A chapter-organized pack: the chapter file wins over the volume fallback.
        var files = new[]
        {
            File("Mato Seihei no Slave c180.cbz"),
            File("Mato Seihei no Slave c181.cbz"),
            File("Mato Seihei no Slave c182.cbz"),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new ComicRequest { Title = "Mato Seihei no Slave", Volume = 22, Chapter = 181 }, files);

        var only = Assert.Single(selected);
        Assert.Contains("c181", only.FileName);
    }

    [Fact]
    public void SpecificComicUnitNotInPackReturnsNothing()
    {
        // Volume 99 isn't in the pack: download nothing rather than every volume.
        var files = new[]
        {
            File("Mato Seihei no Slave v20.cbz"),
            File("Mato Seihei no Slave v21.cbz"),
            File("Mato Seihei no Slave v22.cbz"),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new ComicRequest { Title = "Mato Seihei no Slave", Volume = 99 }, files);

        Assert.Empty(selected);
    }

    [Fact]
    public void GeneralSearchPicksMatchingFileFromPack()
    {
        var files = new[]
        {
            File("ubuntu-22.04-desktop-amd64.iso", 4_000_000),
            File("debian-12-amd64.iso", 3_500_000),
            File("readme.txt", 2_000),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new GeneralRequest { Title = "ubuntu 22.04", Kind = MediaType.Other }, files);

        var only = Assert.Single(selected);
        Assert.Contains("ubuntu", only.FileName);
    }

    [Fact]
    public void GeneralSearchTagPicksFileIndependentOfTitle()
    {
        // Search a big pack by its name, but target one file inside it via the tag.
        var files = new[]
        {
            File("3D Lemmings.bin", 500_000_000),
            File("3D Lemmings.cue", 1_000),
            File("40 Winks.bin", 500_000_000),
            File("40 Winks.cue", 1_000),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new GeneralRequest { Title = "Big Game Pack", FileFilters = ["3D Lemmings"] }, files);

        Assert.Equal(2, selected.Count); // the .bin + .cue for that game
        Assert.All(selected, f => Assert.Contains("3D Lemmings", f.FileName));
    }

    [Fact]
    public void GeneralSearchMultipleTagsGrabEachMatch()
    {
        var files = new[]
        {
            File("3D Lemmings.bin", 500_000_000),
            File("3D Lemmings.cue", 1_000),
            File("40 Winks.bin", 500_000_000),
            File("40 Winks.cue", 1_000),
            File("2Xtreme.bin", 500_000_000),
            File("2Xtreme.cue", 1_000),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new GeneralRequest { Title = "Big Game Pack", FileFilters = ["3D Lemmings", "40 Winks"] }, files);

        Assert.Equal(4, selected.Count); // both games' .bin + .cue
        Assert.Contains(selected, f => f.FileName == "3D Lemmings.bin");
        Assert.Contains(selected, f => f.FileName == "40 Winks.cue");
        Assert.DoesNotContain(selected, f => f.FileName.StartsWith("2Xtreme"));
    }

    [Fact]
    public void GeneralSearchFileTypeRestrictsExtension()
    {
        var files = new[]
        {
            File("Tron.iso", 700_000_000),
            File("Tron.bin", 700_000_000),
            File("Tron.cue", 1_000),
        };

        var selected = MediaFileMatcher.SelectForRequest(
            new GeneralRequest { Title = "Tron", FileType = "iso" }, files);

        var only = Assert.Single(selected);
        Assert.EndsWith(".iso", only.FileName);
    }

    [Fact]
    public void GeneralSearchWithNoMatchReturnsWholePack()
    {
        var files = new[] { File("a.bin"), File("b.bin") };

        var selected = MediaFileMatcher.SelectForRequest(
            new GeneralRequest { Title = "something-else" }, files);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void NonSpecificRequestReturnsAllFiles()
    {
        var files = new[] { File("a.mkv"), File("b.mkv") };

        var movie = MediaFileMatcher.SelectForRequest(new MovieRequest { Title = "X" }, files);
        var fullSeason = MediaFileMatcher.SelectForRequest(new TvRequest { Title = "X", Season = 1 }, files);

        Assert.Equal(2, movie.Count);
        Assert.Equal(2, fullSeason.Count);
    }

    [Fact]
    public void SingleFileReturnedAsIs()
    {
        var files = new[] { File("Show.S01E02.mkv") };
        var selected = MediaFileMatcher.SelectForRequest(
            new TvRequest { Title = "Show", Season = 1, Episode = 2 }, files);
        Assert.Single(selected);
    }
}
