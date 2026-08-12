using System.Reflection;
using System.Text.Json;
using EverythingBox.Server;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>
/// The Task-2 rename moved the record MEMBER <c>CatalogDescriptor.MediaType</c> /
/// <c>CatalogItem.MediaType</c> to <c>Kind</c>. The wire must not have noticed: neither
/// record is ever serialized directly — both go through an anonymous-object projection
/// that hard-codes the JSON key <c>type</c> — so the emitted key stays <c>type</c> and the
/// value is unchanged. These tests exercise the ACTUAL production projections (the manifest
/// builder for descriptors, <c>AddonEndpoints.ToWire</c> for items), not a hand-rolled
/// stand-in, so a future accident that started serializing the record itself — leaking a
/// <c>kind</c> (or a stale <c>mediaType</c>) key — would fail here.
/// </summary>
public class CatalogWireStabilityTests
{
    private static readonly ManifestOptions Options = new(
        Id: "com.everythingbox.server",
        Name: "EverythingBox Server",
        Version: "1.0.0",
        Description: "Test",
        Accent: "#3E8E7E");

    [Fact]
    public void CatalogDescriptor_Kind_is_emitted_on_the_wire_as_type_not_kind()
    {
        // The real /manifest.json projection, called exactly as the route calls it.
        var manifest = new ManifestBuilder().Build(
            Options,
            [new FakeSource("alpha", [new CatalogDescriptor("series", "Series", Kind: "series")])]);
        var json = JsonSerializer.SerializeToElement(manifest);

        var catalog = json.GetProperty("catalogs").EnumerateArray().Single();
        Assert.Equal("series", catalog.GetProperty("type").GetString());
        Assert.False(catalog.TryGetProperty("kind", out _), "the wire must not expose a 'kind' key");
        Assert.False(catalog.TryGetProperty("mediaType", out _), "the wire must not expose a 'mediaType' key");
    }

    [Fact]
    public void CatalogItem_Kind_is_emitted_on_the_wire_as_type_not_kind()
    {
        // AddonEndpoints.ToWire is the real /catalog projection; invoke it directly so this
        // asserts against production code rather than a copy of the anonymous shape.
        var toWire = typeof(AddonEndpoints).GetMethod("ToWire", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(toWire);

        var catalog = new SourceCatalog(
            "Series",
            [new CatalogItem("one", "One", "", Kind: "series", ThumbnailUrl: null, Expandable: true)]);
        var wire = toWire!.Invoke(null, [catalog, "meta"]);
        var json = JsonSerializer.SerializeToElement(wire);

        var item = json.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("series", item.GetProperty("type").GetString());
        Assert.False(item.TryGetProperty("kind", out _), "the wire must not expose a 'kind' key");
        Assert.False(item.TryGetProperty("mediaType", out _), "the wire must not expose a 'mediaType' key");
    }
}
