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

/// <summary>MediaTypes containing a null ELEMENT (as opposed to a null Type string,
/// which the post-loop filter/group-by tolerate fine) — the likelier plugin mistake.
/// Proves C3: the combined mediaTypes list used to be filtered/grouped AFTER the
/// per-source try closed, so a null element here NREs /manifest.json for every source,
/// not just this one.</summary>
file sealed class NullMediaTypeElementSource : IMediaSource
{
    public string Key => "nullmediatype";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];
    public IReadOnlyList<MediaTypeDescriptor> MediaTypes { get; } =
        [null!, new MediaTypeDescriptor("game", "#7A5BD0", "\U0001F579", "game", "poster")];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

/// <summary>
/// C1 (a regression, and NOT a real cancellation): Build() takes no CancellationToken, so
/// nothing it does can be legitimately cancelled — there is no "genuine" cancellation for a
/// "when (ex is not OperationCanceledException)" filter to distinguish from a plugin that
/// simply throws this type. Proves the filter must not exist here at all.
/// </summary>
file sealed class ThrowingOperationCanceledCatalogsSource : IMediaSource
{
    public string Key => "canceled";
    public IReadOnlyList<CatalogDescriptor> Catalogs => throw new OperationCanceledException("Catalogs boom (not really cancelled)");

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty(""));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

/// <summary>A null CatalogDescriptor ELEMENT (as opposed to Catalogs itself being null,
/// which the getter's own non-nullable annotation does not stop a plugin from returning
/// anyway) — the same "null element inside an otherwise-real list" shape as C3's MediaTypes
/// case, one field over. Proves the minor fix: it must be skipped, not NRE the whole
/// source's catalog list away.</summary>
file sealed class NullCatalogDescriptorElementSource : IMediaSource
{
    public string Key => "nullcatalogdescriptor";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } =
        [null!, new CatalogDescriptor("keep", "Keep", "movie")];

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

    // C3: a null element in MediaTypes (not just a throwing getter) used to NRE the
    // combined list's post-loop filter/group-by, 500ing /manifest.json entirely — the
    // exact headline symptom the containment work was meant to eliminate, in the file
    // it was meant to fix. It must be discarded per-source, and the healthy media type
    // from the SAME source must still be declared.
    [Fact]
    public void A_null_MediaTypes_element_is_discarded_but_the_healthy_one_still_declares()
    {
        var json = Build(new NullMediaTypeElementSource());

        var declared = json.GetProperty("mediaTypes").EnumerateArray().ToList();
        Assert.Equal("game", Assert.Single(declared).GetProperty("type").GetString());
    }

    // C1 (regression): "catch (Exception ex) when (ex is not OperationCanceledException)"
    // tested the exception's TYPE, not whether this call was actually cancelled — and Build()
    // has no CancellationToken at all, so nothing here can be legitimately cancelled. A
    // plugin that simply throws OperationCanceledException (its own internal timeout,
    // deliberately, whatever) used to escape containment entirely and 500 /manifest.json for
    // every OTHER installed source.
    [Fact]
    public void A_source_whose_Catalogs_getter_throws_OperationCanceledException_is_omitted_but_others_still_appear()
    {
        var json = Build(
            new FakeSource("alpha", [new CatalogDescriptor("a", "A", "movie")]),
            new ThrowingOperationCanceledCatalogsSource());

        var catalog = json.GetProperty("catalogs").EnumerateArray().Single();
        Assert.Equal("alpha:a", catalog.GetProperty("id").GetString());
    }

    // Minor: a null CatalogDescriptor element used to NRE inside the try (reading c.Id),
    // dropping the WHOLE source's catalog list — including its other, healthy entries —
    // rather than just skipping the one bad element, unlike the equivalent MediaTypes case
    // one field over.
    [Fact]
    public void A_null_CatalogDescriptor_element_is_skipped_but_the_healthy_one_still_appears()
    {
        var json = Build(new NullCatalogDescriptorElementSource());

        var catalog = json.GetProperty("catalogs").EnumerateArray().Single();
        Assert.Equal("nullcatalogdescriptor:keep", catalog.GetProperty("id").GetString());
    }
}
