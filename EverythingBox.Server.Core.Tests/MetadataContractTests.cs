using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Tests;

/// <summary>A metadata source implementing only the required members — proves
/// EpisodesAsync has a working default so a movie-only source need not write it.</summary>
file sealed class MovieOnlySource : IMetadataSource
{
    public string Name => "movies-only";
    public IReadOnlyList<string> SupportedMediaTypes { get; } = ["movie"];

    public Task<IReadOnlyList<MetadataItem>> BrowseAsync(string mediaType, string? query, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MetadataItem>>(
            [new MetadataItem("m1", "Some Film", "movie", Year: 2020)]);
}

public class MetadataContractTests
{
    [Fact]
    public async Task EpisodesAsync_defaults_to_empty()
    {
        IMetadataSource source = new MovieOnlySource();
        Assert.Empty(await source.EpisodesAsync("anything", CancellationToken.None));
    }

    [Fact]
    public void A_metadata_item_carries_what_a_request_needs()
    {
        var item = new MetadataItem("m1", "Some Film", "movie", Year: 2020);
        Assert.Equal("Some Film", item.Title);
        Assert.Equal(2020, item.Year);
    }

    [Fact]
    public void ApiVersion_is_1_4_now_that_the_contract_carries_the_provider_helpers()
    {
        Assert.Equal(1, ServerApi.Current.Major);
        Assert.Equal(4, ServerApi.Current.Minor);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    public void Plugins_built_against_any_earlier_minor_still_load(int major, int minor)
        => Assert.True(ServerApi.IsCompatible(new Version(major, minor)));
}
