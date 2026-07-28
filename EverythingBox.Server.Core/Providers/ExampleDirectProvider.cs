using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Providers;

/// <summary>
/// SKELETON example of a direct provider, to demonstrate the shape a concrete
/// plugin takes. Copy this, point <c>_baseUrl</c> at a real tracker, and fill in
/// the three template methods with that site's query format and response parsing.
/// </summary>
public sealed class ExampleDirectProvider : DirectProviderBase
{
    private readonly Uri _baseUrl;

    public ExampleDirectProvider(HttpClient httpClient, Uri baseUrl)
        : base(httpClient)
        => _baseUrl = baseUrl;

    public override string Name => "Example";

    public override ProviderCapabilities Capabilities { get; } = new()
    {
        SupportedMediaTypes = new HashSet<MediaType> { MediaType.Movie, MediaType.Tv },
        ProvidesMagnet = true,
    };

    protected override string BuildSearchQuery(MediaRequest request)
    {
        // TODO: e.g. "Title (Year)" for movies, "Title S01E02" for TV.
        // The base `request` is strongly typed — pattern-match for specifics:
        //   if (request is TvRequest tv && tv.Season is { } s) { ... }
        throw new NotImplementedException();
    }

    protected override Uri BuildRequestUri(string query, MediaRequest request)
    {
        // TODO: combine _baseUrl + query (+ category mapping) into the search URL.
        throw new NotImplementedException();
    }

    protected override IReadOnlyList<TorrentResult> ParseResponse(string body, MediaRequest request)
    {
        // TODO: parse JSON/HTML into TorrentResult { Title, ProviderName = Name,
        //       MagnetUri, Seeders, SizeBytes, ... }.
        throw new NotImplementedException();
    }
}
