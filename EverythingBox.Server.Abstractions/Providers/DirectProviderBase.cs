namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Base class for "direct" providers — those that talk to a single tracker/site
/// over HTTP, whether via its JSON/RSS API or by scraping HTML. It handles the
/// boilerplate (capability gate, HTTP call, error surface) so a concrete plugin
/// only implements three template methods:
/// <list type="number">
///   <item><see cref="BuildSearchQuery"/> — typed request to a text query.</item>
///   <item><see cref="BuildRequestUri"/> — query to a full backend URL.</item>
///   <item><see cref="ParseResponse"/> — raw body to <see cref="TorrentResult"/>s.</item>
/// </list>
/// See <c>ExampleDirectProvider</c> (EverythingBox.Server.Core.Providers) for the shape of a concrete plugin.
/// </summary>
/// <remarks>
/// This lives in Abstractions, not Core, deliberately: a plugin references only the
/// contract assembly, so a base class in Core would be unreachable to the very
/// authors it exists to serve. Everything it touches — MediaRequest, TorrentResult,
/// ProviderCapabilities — is an Abstractions type, so it carries no Core dependency.
/// </remarks>
public abstract class DirectProviderBase : ITorrentProvider
{
    protected DirectProviderBase(HttpClient httpClient)
    {
        HttpClient = httpClient;
    }

    protected HttpClient HttpClient { get; }

    public abstract string Name { get; }

    public abstract ProviderCapabilities Capabilities { get; }

    public async Task<IReadOnlyList<TorrentResult>> SearchAsync(
        MediaRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.Supports(request.MediaType))
            return [];

        var query = BuildSearchQuery(request);
        var requestUri = BuildRequestUri(query, request);

        using var response = await HttpClient
            .GetAsync(requestUri, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        return ParseResponse(body, request);
    }

    /// <summary>Compose the textual search query from a typed request.</summary>
    protected abstract string BuildSearchQuery(MediaRequest request);

    /// <summary>Build the full backend request URI for the given query.</summary>
    protected abstract Uri BuildRequestUri(string query, MediaRequest request);

    /// <summary>Parse the raw HTTP response body into torrent results.</summary>
    protected abstract IReadOnlyList<TorrentResult> ParseResponse(string body, MediaRequest request);
}
