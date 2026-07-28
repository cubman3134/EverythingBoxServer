using System.Text.RegularExpressions;
using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Core.Torrents;

namespace EverythingBox.Server.Core.Download.QBittorrent;

/// <summary>
/// <see cref="IDownloadClient"/> for qBittorrent via its Web API (v2). Handles
/// the cookie-based login handshake (skipped when no username is configured, for
/// localhost-bypass setups) and posts the release's magnet or <c>.torrent</c> URL
/// to <c>/api/v2/torrents/add</c>.
/// </summary>
public sealed class QBittorrentClient : IDownloadClient
{
    private static readonly Regex MagnetHash =
        new(@"urn:btih:([A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly QBittorrentOptions _options;
    private string? _sid;

    public QBittorrentClient(HttpClient http, QBittorrentOptions options)
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

        if (!await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false))
            return AddTorrentResult.Failed(Name, "authentication failed");

        using var form = new MultipartFormDataContent();
        void Field(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                form.Add(new StringContent(value), name);
        }

        Field("urls", link);
        Field("category", options?.Category ?? _options.DefaultCategory);
        Field("savepath", options?.SavePath ?? _options.DefaultSavePath);
        if (options?.Paused == true)
        {
            Field("paused", "true");  // qBittorrent <= 4.x
            Field("stopped", "true"); // qBittorrent >= 5.x (renamed)
        }
        if (options?.Tags is { Count: > 0 } tags)
            Field("tags", string.Join(',', tags));

        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/v2/torrents/add"))
        {
            Content = form,
        };
        Authorize(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return AddTorrentResult.Failed(Name, $"add failed: HTTP {(int)response.StatusCode}");

        return AddTorrentResult.Ok(Name, InfoHashOf(torrent));
    }

    private async Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        // No username => assume localhost auth bypass; nothing to do.
        if (string.IsNullOrEmpty(_options.Username))
            return true;
        if (_sid is not null)
            return true;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _options.Username,
            ["password"] = _options.Password ?? string.Empty,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/v2/auth/login"))
        {
            Content = form,
        };
        // qBittorrent enforces a same-origin Referer to guard against CSRF.
        request.Headers.Referrer = _options.BaseUrl;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return false;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!body.Contains("Ok", StringComparison.OrdinalIgnoreCase))
            return false;

        _sid = ParseSid(response);
        return true;
    }

    private void Authorize(HttpRequestMessage request)
    {
        request.Headers.Referrer = _options.BaseUrl;
        if (_sid is not null)
            request.Headers.TryAddWithoutValidation("Cookie", $"SID={_sid}");
    }

    private Uri Url(string absolutePath) => new(_options.BaseUrl, absolutePath);

    private static string? ParseSid(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return null;

        foreach (var cookie in cookies)
        {
            var idx = cookie.IndexOf("SID=", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                continue;

            var rest = cookie[(idx + 4)..];
            var end = rest.IndexOf(';');
            return end >= 0 ? rest[..end] : rest;
        }

        return null;
    }

    private static string? InfoHashOf(TorrentResult torrent)
    {
        if (!string.IsNullOrWhiteSpace(torrent.InfoHash))
            return torrent.InfoHash.Trim().ToLowerInvariant();

        if (torrent.MagnetUri is not null && MagnetHash.Match(torrent.MagnetUri.OriginalString) is { Success: true } m)
            return m.Groups[1].Value.ToLowerInvariant();

        return null;
    }
}
