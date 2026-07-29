using EverythingBox.Server.Abstractions;

namespace TestPlugin.Metadata;

/// <summary>Fixture metadata source for <c>BrowseServerFactory</c> (see
/// <c>EverythingBox.Server.Tests/BrowseToStreamTests.cs</c>): one film and one series
/// with two episodes, generically named so nothing here ever names a real title.
/// Registers ONLY via <see cref="IPluginRegistry.AddMetadata"/> — no <c>AddSource</c> —
/// so a browse fixture can prove the "meta:" catalogs come from the metadata tier alone,
/// with the indexer/debrid pipeline supplying the actual playable release.</summary>
public sealed class TestMetadataSource : IMetadataSource
{
    public string Name => "test-metadata";
    public IReadOnlyList<string> SupportedMediaTypes { get; } = ["movie", "series"];

    private static readonly MetadataItem Film = new(
        "film-1", "Example Film", "movie", PosterUrl: "https://example.test/poster.jpg");

    private static readonly MetadataItem Series = new("series-1", "Example Show", "series");

    private static readonly MetadataEpisode[] Episodes =
    [
        new MetadataEpisode(1, 1, "Pilot"),
        new MetadataEpisode(1, 2, "Second"),
    ];

    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MetadataItem>>(mediaType switch
        {
            "movie" => [Film],
            "series" => [Series],
            _ => [],
        });

    public Task<IReadOnlyList<MetadataEpisode>> EpisodesAsync(string seriesId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MetadataEpisode>>(seriesId == Series.Id ? Episodes : []);
}

public sealed class MetadataTestPlugin : IPlugin
{
    public string Key => "browsemeta";
    public string DisplayName => "Browse Metadata Test Plugin";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context) =>
        registry.AddMetadata(new TestMetadataSource());
}
