using System.Text.Json;
using EverythingBox.Server;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Every member reached during manifest-building is plugin-authored code and
/// can throw — this double proves the Catalogs getter specifically.</summary>
file sealed class ThrowingCatalogsSource : IMediaSource
{
    public string Key => "throwing";
    public IReadOnlyList<CatalogDescriptor> Catalogs => throw new InvalidOperationException("Catalogs boom");

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

public class ManifestBuilderTests
{
    private static readonly ManifestOptions Options = new(
        Id: "com.everythingbox.server",
        Name: "EverythingBox Server",
        Version: "1.0.0",
        Description: "Test",
        Accent: "#3E8E7E");

    private static JsonElement Build(params IMediaSource[] sources)
    {
        var manifest = new ManifestBuilder().Build(Options, sources);
        return JsonSerializer.SerializeToElement(manifest);
    }

    [Fact]
    public void Prefixes_catalog_ids_with_the_source_key()
    {
        var json = Build(new FakeSource("alpha", [new CatalogDescriptor("movies", "Movies", "movie")]));

        var catalog = json.GetProperty("catalogs").EnumerateArray().Single();
        Assert.Equal("alpha:movies", catalog.GetProperty("id").GetString());
        Assert.Equal("Movies", catalog.GetProperty("name").GetString());
        Assert.Equal("movie", catalog.GetProperty("type").GetString());
    }

    [Fact]
    public void Declares_non_builtin_media_types_once()
    {
        var game = new MediaTypeDescriptor("game", "#7A5BD0", "\U0001F579", "game", "poster");
        var json = Build(
            new FakeSource("alpha", [new CatalogDescriptor("g1", "Games", "game")], [game]),
            new FakeSource("beta", [new CatalogDescriptor("g2", "More Games", "game")], [game]));

        var declared = json.GetProperty("mediaTypes").EnumerateArray().ToList();
        Assert.Equal("game", Assert.Single(declared).GetProperty("type").GetString());
    }

    [Fact]
    public void Never_declares_movie_or_series()
    {
        // The client knows these natively; declaring them confuses it.
        var bogus = new MediaTypeDescriptor("movie", "#fff", "M", "video", "poster");
        var json = Build(new FakeSource("alpha", [new CatalogDescriptor("m", "Movies", "movie")], [bogus]));

        Assert.Empty(json.GetProperty("mediaTypes").EnumerateArray());
    }

    [Fact]
    public void Is_a_media_source_manifest_with_no_sources_at_all()
    {
        var json = Build();

        Assert.Equal("media-source", json.GetProperty("type").GetString());
        Assert.Equal("com.everythingbox.server", json.GetProperty("id").GetString());
        Assert.Empty(json.GetProperty("catalogs").EnumerateArray());
    }

    [Fact]
    public void Keeps_catalogs_in_source_order()
    {
        var json = Build(
            new FakeSource("alpha", [new CatalogDescriptor("a", "A", "movie"), new CatalogDescriptor("b", "B", "movie")]),
            new FakeSource("beta", [new CatalogDescriptor("c", "C", "movie")]));

        var ids = json.GetProperty("catalogs").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.Equal(["alpha:a", "alpha:b", "beta:c"], ids);
    }

    // F1: a plugin's Catalogs getter is untrusted code and can throw on any request. One
    // misbehaving source must not turn /manifest.json into a 500 for every other, healthy
    // source — it must simply be omitted while everything else still comes back.
    [Fact]
    public void A_source_whose_Catalogs_getter_throws_is_omitted_but_others_still_appear()
    {
        var json = Build(
            new FakeSource("alpha", [new CatalogDescriptor("a", "A", "movie")]),
            new ThrowingCatalogsSource());

        var catalog = json.GetProperty("catalogs").EnumerateArray().Single();
        Assert.Equal("alpha:a", catalog.GetProperty("id").GetString());
    }
}
