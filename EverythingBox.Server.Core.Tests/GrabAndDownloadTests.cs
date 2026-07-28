using EverythingBox.Server.Abstractions;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class GrabAndDownloadTests
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
    public async Task GrabsBestThenSendsToClient()
    {
        var client = new RecordingClient();
        var grabber = new GrabberBuilder()
            .AddProvider(new StubProvider([Bluray]))
            .UseDownloadClient(client)
            .Build();

        var result = await grabber.GrabAndDownloadAsync(
            new MovieRequest { Title = "The Matrix", Year = 1999 },
            new DownloadOptions { Category = "movies" });

        Assert.True(result.Found);
        Assert.True(result.Sent);
        Assert.Equal("H1", client.LastTorrent!.InfoHash);
        Assert.Equal("movies", client.LastOptions!.Category);
    }

    [Fact]
    public async Task NoMatchMeansNothingSent()
    {
        var client = new RecordingClient();
        var grabber = new GrabberBuilder()
            .AddProvider(new StubProvider([])) // nothing found
            .UseDownloadClient(client)
            .Build();

        var result = await grabber.GrabAndDownloadAsync(new MovieRequest { Title = "The Matrix" });

        Assert.False(result.Found);
        Assert.False(result.Sent);
        Assert.Null(result.Download);
        Assert.Null(client.LastTorrent);
    }

    [Fact]
    public async Task ThrowsWhenNoClientConfigured()
    {
        var grabber = new GrabberBuilder().AddProvider(new StubProvider([Bluray])).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grabber.GrabAndDownloadAsync(new MovieRequest { Title = "The Matrix" }));
    }

    private sealed class RecordingClient : IDownloadClient
    {
        public TorrentResult? LastTorrent { get; private set; }
        public DownloadOptions? LastOptions { get; private set; }

        public string Name => "recording";

        public Task<AddTorrentResult> AddAsync(TorrentResult torrent, DownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastTorrent = torrent;
            LastOptions = options;
            return Task.FromResult(AddTorrentResult.Ok(Name, torrent.InfoHash));
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
