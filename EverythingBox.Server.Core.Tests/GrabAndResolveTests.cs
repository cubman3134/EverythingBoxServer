using EverythingBox.Server.Abstractions;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class GrabAndResolveTests
{
    private static TorrentResult Bluray => new()
    {
        Title = "The Matrix 1999 1080p BluRay",
        ProviderName = "p",
        InfoHash = "H1",
        MagnetUri = new Uri("magnet:?xt=urn:btih:H1"),
        Seeders = 50,
    };

    [Fact]
    public async Task GrabsBestThenResolvesThroughDebrid()
    {
        var debrid = new RecordingDebrid();
        var grabber = new GrabberBuilder()
            .AddProvider(new StubProvider([Bluray]))
            .UseDebridService(debrid)
            .Build();

        var result = await grabber.GrabAndResolveAsync(new MovieRequest { Title = "The Matrix", Year = 1999 });

        Assert.True(result.Found);
        Assert.True(result.Resolved);
        Assert.Equal("H1", debrid.LastTorrent!.InfoHash);
        Assert.Single(result.Debrid!.Links);
    }

    [Fact]
    public async Task NoMatchMeansNothingResolved()
    {
        var debrid = new RecordingDebrid();
        var grabber = new GrabberBuilder()
            .AddProvider(new StubProvider([]))
            .UseDebridService(debrid)
            .Build();

        var result = await grabber.GrabAndResolveAsync(new MovieRequest { Title = "The Matrix" });

        Assert.False(result.Found);
        Assert.False(result.Resolved);
        Assert.Null(result.Debrid);
        Assert.Null(debrid.LastTorrent);
    }

    [Fact]
    public async Task ThrowsWhenNoDebridConfigured()
    {
        var grabber = new GrabberBuilder().AddProvider(new StubProvider([Bluray])).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grabber.GrabAndResolveAsync(new MovieRequest { Title = "The Matrix" }));
    }

    private sealed class RecordingDebrid : IDebridService
    {
        public TorrentResult? LastTorrent { get; private set; }

        public string Name => "recording";

        public Task<DebridResult> ResolveAsync(TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
        {
            LastTorrent = torrent;
            var links = new[] { new DebridLink("movie.mkv", new Uri("http://x/movie.mkv"), 1) };
            return Task.FromResult(DebridResult.Resolved(Name, "T1", cached: true, links));
        }
    }

    private sealed class StubProvider(IReadOnlyList<TorrentResult> items) : ITorrentProvider
    {
        public string Name => "stub";

        public ProviderCapabilities Capabilities { get; } = new()
        {
            SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv, MediaType.Music },
        };

        public Task<IReadOnlyList<TorrentResult>> SearchAsync(MediaRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(items);
    }
}
