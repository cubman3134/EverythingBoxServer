using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Tests;

public class MediaTypeNamesTests
{
    [Theory]
    [InlineData(MediaType.Movie, "movie")]
    [InlineData(MediaType.Tv, "series")]        // the names genuinely differ
    [InlineData(MediaType.Music, "music")]
    [InlineData(MediaType.Audiobook, "audiobook")]
    [InlineData(MediaType.Book, "book")]
    [InlineData(MediaType.Comic, "comic")]
    [InlineData(MediaType.PcGame, "game")]
    public void Maps_an_enum_member_to_its_protocol_string(MediaType type, string expected)
        => Assert.Equal(expected, MediaTypeNames.ToProtocolString(type));

    [Fact]
    public void Other_has_no_protocol_string()
    {
        // "Other" is a pipeline-side catch-all with nothing to show a client.
        Assert.Null(MediaTypeNames.ToProtocolString(MediaType.Other));
    }

    [Theory]
    [InlineData("movie", MediaType.Movie)]
    [InlineData("series", MediaType.Tv)]
    [InlineData("music", MediaType.Music)]
    [InlineData("audiobook", MediaType.Audiobook)]
    [InlineData("book", MediaType.Book)]
    [InlineData("comic", MediaType.Comic)]
    [InlineData("manga", MediaType.Comic)]      // many-to-one, deliberately
    [InlineData("game", MediaType.PcGame)]
    public void Parses_a_protocol_string_to_its_enum_member(string protocol, MediaType expected)
    {
        Assert.True(MediaTypeNames.TryParseProtocol(protocol, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Parses_case_insensitively()
    {
        Assert.True(MediaTypeNames.TryParseProtocol("SERIES", out var actual));
        Assert.Equal(MediaType.Tv, actual);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_an_unknown_protocol_string(string? protocol)
        => Assert.False(MediaTypeNames.TryParseProtocol(protocol, out _));

    [Fact]
    public void Every_enum_member_except_Other_round_trips()
    {
        // Guards the case where someone adds an enum member and forgets the mapping.
        foreach (var type in Enum.GetValues<MediaType>())
        {
            if (type == MediaType.Other) continue;

            var protocol = MediaTypeNames.ToProtocolString(type);
            Assert.True(protocol is not null, $"{type} has no protocol string.");
            Assert.True(MediaTypeNames.TryParseProtocol(protocol, out var back), $"'{protocol}' does not parse back.");
            Assert.Equal(type, back);
        }
    }

    [Fact]
    public void Manga_parses_to_Comic_but_Comic_does_not_emit_manga()
    {
        // The many-to-one direction has to pick one, and "comic" is it.
        Assert.True(MediaTypeNames.TryParseProtocol("manga", out var parsed));
        Assert.Equal(MediaType.Comic, parsed);
        Assert.Equal("comic", MediaTypeNames.ToProtocolString(MediaType.Comic));
    }

    [Fact]
    public void Each_protocol_constant_equals_its_literal_value()
    {
        // The consts are the single source of truth for the client vocabulary; pin their values.
        Assert.Equal("movie", MediaTypeNames.Movie);
        Assert.Equal("series", MediaTypeNames.Series);
        Assert.Equal("comic", MediaTypeNames.Comic);
        Assert.Equal("manga", MediaTypeNames.Manga);
        Assert.Equal("book", MediaTypeNames.Book);
        Assert.Equal("audiobook", MediaTypeNames.Audiobook);
        Assert.Equal("music", MediaTypeNames.Music);
        Assert.Equal("game", MediaTypeNames.Game);
        Assert.Equal("platform", MediaTypeNames.Platform);
    }

    [Fact]
    public void Platform_does_not_parse_to_an_enum_member()
    {
        // Intentional regression guard: "platform" is a client-only container type with no
        // MediaType member, so it must never resolve to a pipeline enum value.
        Assert.False(MediaTypeNames.TryParseProtocol("platform", out _));
    }
}
