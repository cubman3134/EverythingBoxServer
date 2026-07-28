using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Torrents;

namespace EverythingBox.Server.Core.Download.Transmission;

/// <summary>
/// <see cref="IDownloadClient"/> for the Transmission daemon via its JSON-RPC API.
/// Handles the CSRF session-id handshake (a 409 response carries the
/// <c>X-Transmission-Session-Id</c> to echo back) and optional HTTP Basic auth,
/// then issues a <c>torrent-add</c> with the release's magnet or <c>.torrent</c> URL.
/// </summary>
public sealed class TransmissionClient : IDownloadClient
{
    private readonly HttpClient _http;
    private readonly TransmissionOptions _options;
    private string? _sessionId;

    public TransmissionClient(HttpClient http, TransmissionOptions options)
    {
        _http = http;
        _options = options;
    }

    public string Name => _options.Name;

    public async Task<AddTorrentResult> AddAsync(
        TorrentResult torrent,
        DownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var link = torrent.MagnetUri?.ToString() ?? torrent.DownloadUrl?.ToString();
        if (string.IsNullOrEmpty(link))
            return AddTorrentResult.Failed(Name, "release has no magnet or download URL");

        var arguments = new JsonObject { ["filename"] = link, ["paused"] = options?.Paused ?? false };

        var downloadDir = options?.SavePath ?? _options.DefaultDownloadDir;
        if (!string.IsNullOrEmpty(downloadDir))
            arguments["download-dir"] = downloadDir;

        if (options?.Tags is { Count: > 0 } tags)
            arguments["labels"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray());

        var body = new JsonObject { ["method"] = "torrent-add", ["arguments"] = arguments }.ToJsonString();

        using var response = await PostAsync(body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return AddTorrentResult.Failed(Name, $"add failed: HTTP {(int)response.StatusCode}");

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseAddResponse(responseBody, torrent);
    }

    private async Task<HttpResponseMessage> PostAsync(string json, CancellationToken cancellationToken)
    {
        // First attempt may 409 with a fresh session id to echo back; retry once.
        for (var attempt = 0; ; attempt++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, RpcUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            Authorize(request);

            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Conflict && attempt == 0)
            {
                if (response.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
                    _sessionId = ids.FirstOrDefault();
                response.Dispose();
                request.Dispose();
                continue;
            }

            request.Dispose();
            return response;
        }
    }

    private AddTorrentResult ParseAddResponse(string responseBody, TorrentResult torrent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var result = root.TryGetProperty("result", out var r) ? r.GetString() : null;
            if (!string.Equals(result, "success", StringComparison.OrdinalIgnoreCase))
                return AddTorrentResult.Failed(Name, result ?? "unknown RPC error");

            string? hash = null;
            if (root.TryGetProperty("arguments", out var args))
            {
                foreach (var key in (ReadOnlySpan<string>)["torrent-added", "torrent-duplicate"])
                {
                    if (args.TryGetProperty(key, out var added)
                        && added.TryGetProperty("hashString", out var h))
                    {
                        hash = h.GetString();
                        break;
                    }
                }
            }

            return AddTorrentResult.Ok(Name, hash ?? torrent.InfoHash);
        }
        catch (JsonException)
        {
            return AddTorrentResult.Failed(Name, "could not parse RPC response");
        }
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("X-Transmission-Session-Id", _sessionId);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    private Uri RpcUrl => new(_options.BaseUrl, _options.RpcPath);
}
