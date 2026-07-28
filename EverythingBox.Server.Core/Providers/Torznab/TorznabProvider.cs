using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Providers.Torznab;

/// <summary>
/// An <see cref="ITorrentProvider"/> backed by a Torznab endpoint (Prowlarr,
/// Jackett, or any Torznab-speaking indexer). This is how the library fronts the
/// entire *arr indexer ecosystem: point it at a Prowlarr instance and it can
/// search every indexer Prowlarr aggregates. Reuses <see cref="DirectProviderBase"/>
/// for the HTTP plumbing and delegates the protocol specifics to
/// <see cref="TorznabQueryBuilder"/> and <see cref="TorznabFeedParser"/>.
/// </summary>
public sealed class TorznabProvider : DirectProviderBase
{
    private readonly TorznabOptions _options;

    public TorznabProvider(HttpClient httpClient, TorznabOptions options)
        : base(httpClient)
    {
        _options = options;
        Capabilities = new ProviderCapabilities
        {
            SupportedMediaTypes = options.SupportedMediaTypes,
            RequiresAuthentication = options.ApiKey is not null,
            ProvidesMagnet = true,
            ProvidesTorrentFile = true,
        };
    }

    public override string Name => _options.Name;

    public override ProviderCapabilities Capabilities { get; }

    protected override string BuildSearchQuery(MediaRequest request)
        => TorznabQueryBuilder.BuildSearchTerm(request);

    protected override Uri BuildRequestUri(string query, MediaRequest request)
        => TorznabQueryBuilder.BuildUri(_options, query, request);

    protected override IReadOnlyList<TorrentResult> ParseResponse(string body, MediaRequest request)
        => TorznabFeedParser.Parse(body, Name);
}
