using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// The part id is what a client sends back when it reaches one file of a release, so the
/// two properties that matter are that it ROUND TRIPS whatever a torrent chose to call a
/// file, and that decoding something that is not one is a plain "no" rather than a throw.
/// </summary>
public class ReleasePartIdTests
{
    [Theory]
    [InlineData("01 - Chapter One.mp3")]
    [InlineData("Book/Disc 1/10 - part.mp3")]                      // separators in the name
    [InlineData("A Tale of Two Cities - 100% complete.m4b")]       // percent, which a url would eat
    [InlineData("q?=a&b#frag.mp3")]                                // query/fragment characters
    [InlineData("Émile Zola — Chapitre 3.mp3")]                    // non-ASCII
    [InlineData("~tilde~in~the~name~.mp3")]                        // the separator itself
    public void A_file_name_round_trips_whatever_it_is_called(string fileName)
    {
        var id = ReleasePartId.Encode("cmVsZWFzZQ", fileName);

        Assert.True(ReleasePartId.TryDecode(id, out var release, out var decoded));
        Assert.Equal("cmVsZWFzZQ", release);
        Assert.Equal(fileName, decoded);
    }

    [Fact]
    public void A_plain_release_id_is_not_a_part_id()
    {
        // The case every caller hits most often: an ordinary release id must fall through
        // to the whole-release resolve, not be mistaken for a file inside one.
        Assert.False(ReleasePartId.IsPartId("cmVsZWFzZQ"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("~")]                    // separator only
    [InlineData("~name")]                // no release half
    [InlineData("release~")]             // no file half
    [InlineData("release~!!!not-base64")] // undecodable tail
    public void Anything_that_is_not_a_part_id_decodes_to_nothing_rather_than_throwing(string? id)
    {
        Assert.False(ReleasePartId.TryDecode(id, out var release, out var fileName));
        Assert.Equal(string.Empty, release);
        Assert.Equal(string.Empty, fileName);
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_refused_rather_than_turned_into_replacement_characters()
    {
        // 0xC3 0x28 is an invalid UTF-8 sequence. Decoding it with the default fallback
        // yields "�(", a name that matches no file in any release — a failure one
        // layer further on with nothing left to say why it happened.
        var tail = Convert.ToBase64String(new byte[] { 0xC3, 0x28 }).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(ReleasePartId.TryDecode("release" + ReleasePartId.Separator + tail, out _, out _));
    }

    [Fact]
    public void A_release_id_containing_the_separator_is_refused_at_mint_time()
    {
        // Minting it would produce an id whose halves cannot be told apart on the way back,
        // and the wrong half would name a file. Better no row than a row that resolves to
        // something else.
        Assert.Equal(string.Empty, ReleasePartId.Encode("has~tilde", "01.mp3"));
    }

    [Fact]
    public void An_id_with_no_release_or_no_file_is_not_minted()
    {
        Assert.Equal(string.Empty, ReleasePartId.Encode("", "01.mp3"));
        Assert.Equal(string.Empty, ReleasePartId.Encode("release", ""));
    }

    [Fact]
    public void The_encoded_half_never_contains_the_separator_so_the_split_is_unambiguous()
    {
        // base64url's alphabet is A-Z a-z 0-9 '-' '_' — '~' cannot occur in it, which is
        // the whole reason that character was chosen. Asserted over a name that is nothing
        // but tildes, the worst case for the claim.
        var id = ReleasePartId.Encode("rel", new string('~', 64));

        Assert.Equal(1, id.Count(c => c == ReleasePartId.Separator));
    }

    // ---- matching a part id back to one of the release's files ------------------------

    [Fact]
    public void A_name_matches_itself_exactly()
    {
        Assert.Equal("Part 02.mp3", ReleasePartId.MatchFileName(["Part 01.mp3", "Part 02.mp3"], "Part 02.mp3"));
    }

    [Fact]
    public void A_name_whose_case_a_service_normalised_still_matches()
    {
        Assert.Equal("PART 02.MP3", ReleasePartId.MatchFileName(["PART 01.MP3", "PART 02.MP3"], "Part 02.mp3"));
    }

    [Fact]
    public void A_listing_that_gained_a_folder_prefix_still_matches_on_the_leaf()
    {
        Assert.Equal("Book/Part 02.mp3", ReleasePartId.MatchFileName(["Book/Part 01.mp3", "Book/Part 02.mp3"], "Part 02.mp3"));
    }

    [Fact]
    public void Two_files_with_the_same_leaf_name_are_refused_rather_than_guessed()
    {
        // Disc 1's 01.mp3 and disc 2's are different parts. A wrong part plays perfectly,
        // which is exactly what made this defect so hard to notice in the first place.
        Assert.Null(ReleasePartId.MatchFileName(["Disc 1/01.mp3", "Disc 2/01.mp3"], "01.mp3"));
    }

    [Fact]
    public void An_exact_match_wins_over_an_ambiguous_leaf()
    {
        Assert.Equal("01.mp3", ReleasePartId.MatchFileName(["Disc 1/01.mp3", "01.mp3", "Disc 2/01.mp3"], "01.mp3"));
    }

    [Fact]
    public void A_name_that_is_not_in_the_release_matches_nothing()
    {
        Assert.Null(ReleasePartId.MatchFileName(["Part 01.mp3"], "Part 99.mp3"));
        Assert.Null(ReleasePartId.MatchFileName([], "Part 01.mp3"));
        Assert.Null(ReleasePartId.MatchFileName(["Part 01.mp3"], null));
    }
}
