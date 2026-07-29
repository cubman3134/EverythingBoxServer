using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

file sealed class StubDebrid(DebridResult result) : IDebridService
{
    public string Name => "stub";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => Task.FromResult(result);
}

public class ReleaseStreamResolverTests
{
    private static TorrentResult Release() => new()
    {
        Title = "Some Release 1080p",
        ProviderName = "test-indexer",   // required by TorrentResult
        InfoHash = "0123456789abcdef0123456789abcdef01234567",
    };

    private static ReleaseStreamResolver Resolver(DebridResult? result) =>
        new(result is null ? null : new StubDebrid(result), NullLogger<ReleaseStreamResolver>.Instance);

    private static DebridResult Resolved(params DebridLink[] links)
        => DebridResult.Resolved("stub", "id", cached: true, links);

    [Fact]
    public async Task Without_a_debrid_service_there_is_nothing_to_play()
    {
        var stream = await Resolver(null).ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);
        Assert.Null(stream);
    }

    [Fact]
    public async Task A_resolved_release_becomes_a_playable_stream()
    {
        var resolver = Resolver(Resolved(new DebridLink("Movie.mkv", new Uri("https://example.test/a.mkv"), 100)));

        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("https://example.test/a.mkv", stream!.Url);
        Assert.Equal("video/x-matroska", stream.Mime);
    }

    [Fact]
    public async Task The_index_selects_a_later_link_so_a_user_can_reject_one()
    {
        var resolver = Resolver(Resolved(
            new DebridLink("a.mkv", new Uri("https://example.test/a.mkv"), 1),
            new DebridLink("b.mkv", new Uri("https://example.test/b.mkv"), 2)));

        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 1, CancellationToken.None);

        Assert.Equal("https://example.test/b.mkv", stream!.Url);
    }

    [Fact]
    public async Task An_index_past_the_end_yields_nothing_rather_than_throwing()
    {
        var resolver = Resolver(Resolved(new DebridLink("a.mkv", new Uri("https://example.test/a.mkv"), 1)));
        Assert.Null(await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 9, CancellationToken.None));
    }

    [Fact]
    public async Task A_pending_release_returns_a_notice_the_user_can_act_on()
    {
        var resolver = Resolver(DebridResult.Pending("stub", "id", "caching"));

        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);

        Assert.NotNull(stream);
        Assert.Equal("", stream!.Url);
        Assert.False(string.IsNullOrWhiteSpace(stream.Notice));
    }

    [Fact]
    public async Task A_failed_resolution_yields_nothing()
    {
        var resolver = Resolver(DebridResult.Failed("stub", "nope"));
        Assert.Null(await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None));
    }

    [Fact]
    public async Task A_debrid_service_that_throws_is_contained()
    {
        // Debrid is a network call to someone else's service. It failing must not 500.
        var resolver = new ReleaseStreamResolver(new ThrowingDebrid(), NullLogger<ReleaseStreamResolver>.Instance);
        Assert.Null(await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData("a.mkv", "video/x-matroska")]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.epub", "application/epub+zip")]
    [InlineData("a.unknownext", "application/octet-stream")]
    public async Task Mime_follows_the_file_extension(string fileName, string expected)
    {
        var resolver = Resolver(Resolved(new DebridLink(fileName, new Uri("https://example.test/f"), 1)));
        var stream = await resolver.ResolveAsync(Release(), new MovieRequest { Title = "x" }, 0, CancellationToken.None);
        Assert.Equal(expected, stream!.Mime);
    }
}

file sealed class ThrowingDebrid : IDebridService
{
    public string Name => "throwing";
    public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        => throw new HttpRequestException("upstream is down");
}
