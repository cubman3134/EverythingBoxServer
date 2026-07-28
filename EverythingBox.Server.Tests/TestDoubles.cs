using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Shared IMediaSource stand-in. Later tasks reuse this rather than
/// declaring their own near-identical double.</summary>
internal sealed class FakeSource(
    string key,
    IReadOnlyList<CatalogDescriptor>? catalogs = null,
    IReadOnlyList<MediaTypeDescriptor>? mediaTypes = null) : IMediaSource
{
    public string Key => key;

    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } =
        catalogs ?? [new CatalogDescriptor("c", "C", "movie")];

    public IReadOnlyList<MediaTypeDescriptor> MediaTypes { get; } = mediaTypes ?? [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("C"));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(SourceCatalog.Empty("C"));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}
