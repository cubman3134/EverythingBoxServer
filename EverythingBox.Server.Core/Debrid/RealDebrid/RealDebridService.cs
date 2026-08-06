using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Debrid.RealDebrid;

/// <summary>
/// <see cref="IDebridService"/> for Real-Debrid. Drives the standard flow:
/// <c>addMagnet</c> → (wait for file list) → <c>selectFiles</c> → poll
/// <c>torrents/info</c> → (once downloaded) <c>unrestrict/link</c> for each file.
/// When the request targets a specific episode/track, only the matching file is
/// selected, so a season pack / album downloads just that one file rather than the
/// whole release. Already-cached torrents resolve in one pass; uncached ones return
/// <see cref="DebridStatus.Pending"/> unless <see cref="RealDebridOptions.MaxWait"/>
/// allows waiting.
/// </summary>
public sealed class RealDebridService : IDebridService
{
    private const int FileListAttempts = 20;
    private static readonly string[] TerminalErrors = ["error", "magnet_error", "virus", "dead"];

    private readonly HttpClient _http;
    private readonly RealDebridOptions _options;
    private readonly Uri _base;

    public RealDebridService(HttpClient http, RealDebridOptions options)
    {
        _http = http;
        _options = options;
        _base = EnsureTrailingSlash(options.BaseUrl);
    }

    public string Name => _options.Name;

    public async Task<DebridResult> ResolveAsync(
        TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
    {
        var magnet = await MagnetResolver.ResolveAsync(_http, torrent, cancellationToken).ConfigureAwait(false);
        if (magnet is null)
            return DebridResult.Failed(Name, "release has no magnet, info hash, or .torrent file");

        try
        {
            // 1. Add the magnet.
            var (addOk, addBody) = await SendAsync(HttpMethod.Post, "torrents/addMagnet", Form(("magnet", magnet)), cancellationToken)
                .ConfigureAwait(false);
            if (!addOk)
                return DebridResult.Failed(Name, $"addMagnet: {Summarize(addBody)}");

            string? id;
            using (var doc = JsonDocument.Parse(addBody))
                id = GetString(doc.RootElement, "id");
            if (string.IsNullOrEmpty(id))
                return DebridResult.Failed(Name, "addMagnet returned no torrent id");

            // 2. Wait for the magnet to convert so the file list is available.
            for (var i = 0; ; i++)
            {
                var (infoOk, infoBody) = await SendAsync(HttpMethod.Get, $"torrents/info/{id}", null, cancellationToken)
                    .ConfigureAwait(false);
                if (!infoOk)
                    return DebridResult.Failed(Name, $"info: {Summarize(infoBody)}", id);

                string status;
                IReadOnlyList<RdFile> files;
                IReadOnlyList<string> restricted;
                using (var doc = JsonDocument.Parse(infoBody))
                {
                    status = GetString(doc.RootElement, "status") ?? string.Empty;
                    files = ReadFiles(doc.RootElement);
                    restricted = ReadLinks(doc.RootElement);
                }

                // Already in the account and finished (all files selected): unrestrict now.
                if (status == "downloaded")
                    return await UnrestrictAsync(id, cached: true, restricted, cancellationToken).ConfigureAwait(false);
                if (Array.IndexOf(TerminalErrors, status) >= 0)
                    return DebridResult.Failed(Name, $"torrent {status}", id);

                if (files.Count > 0)
                {
                    // 3. Select only the requested file(s) — or all when not specific.
                    var selection = DetermineSelection(request, files);
                    var (selOk, selBody) = await SendAsync(HttpMethod.Post, $"torrents/selectFiles/{id}", Form(("files", selection)), cancellationToken)
                        .ConfigureAwait(false);
                    if (!selOk)
                        return DebridResult.Failed(Name, $"selectFiles: {Summarize(selBody)}", id);
                    break;
                }

                if (i >= FileListAttempts)
                    return DebridResult.Pending(Name, id, $"still preparing (status: {status})");
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            // 4. Poll until the selected files are downloaded (or give up per MaxWait).
            var stopwatch = Stopwatch.StartNew();
            var waited = false;
            while (true)
            {
                var (infoOk, infoBody) = await SendAsync(HttpMethod.Get, $"torrents/info/{id}", null, cancellationToken)
                    .ConfigureAwait(false);
                if (!infoOk)
                    return DebridResult.Failed(Name, $"info: {Summarize(infoBody)}", id);

                string status;
                IReadOnlyList<string> restricted;
                using (var doc = JsonDocument.Parse(infoBody))
                {
                    status = GetString(doc.RootElement, "status") ?? string.Empty;
                    restricted = ReadLinks(doc.RootElement);
                }

                if (status == "downloaded")
                    return await UnrestrictAsync(id, cached: !waited, restricted, cancellationToken).ConfigureAwait(false);
                if (Array.IndexOf(TerminalErrors, status) >= 0)
                    return DebridResult.Failed(Name, $"torrent {status}", id);

                if (stopwatch.Elapsed >= _options.MaxWait)
                    return DebridResult.Pending(Name, id, $"not cached (status: {status})");

                waited = true;
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return DebridResult.Failed(Name, "could not parse Real-Debrid response");
        }
    }

    private readonly record struct RdFile(int Id, string Path, long? Bytes);

    private static string DetermineSelection(MediaRequest? request, IReadOnlyList<RdFile> files)
    {
        if (request is null || files.Count == 0)
            return "all";

        var matched = MediaFileMatcher.Select(request, files, f => f.Path, f => f.Bytes);
        if (matched.Count == 0 || matched.Count == files.Count)
            return "all";

        return string.Join(',', matched.Select(f => f.Id));
    }

    private static IReadOnlyList<RdFile> ReadFiles(JsonElement root)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<RdFile>();
        foreach (var f in files.EnumerateArray())
        {
            var id = f.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.Number ? idv.GetInt32() : 0;
            var path = f.TryGetProperty("path", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString()! : string.Empty;
            long? bytes = f.TryGetProperty("bytes", out var bv) && bv.ValueKind == JsonValueKind.Number ? bv.GetInt64() : null;
            if (id > 0 && path.Length > 0)
                result.Add(new RdFile(id, path, bytes));
        }

        return result;
    }

    private async Task<DebridResult> UnrestrictAsync(
        string id, bool cached, IReadOnlyList<string> restricted, CancellationToken cancellationToken)
    {
        if (!_options.UnrestrictLinks)
        {
            var raw = restricted
                .Select(ToRawLink)
                .Where(l => l is not null)
                .Select(l => l!)
                .ToList();
            return DebridResult.Resolved(Name, id, cached, raw);
        }

        var links = new List<DebridLink>();
        foreach (var link in restricted)
        {
            var (ok, body) = await SendAsync(HttpMethod.Post, "unrestrict/link", Form(("link", link)), cancellationToken)
                .ConfigureAwait(false);
            if (!ok)
                continue;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var download = GetString(root, "download");
            if (download is null || !Uri.TryCreate(download, UriKind.Absolute, out var url))
                continue;

            links.Add(new DebridLink(GetString(root, "filename") ?? FileNameOf(url), url, GetLong(root, "filesize")));
        }

        return DebridResult.Resolved(Name, id, cached, links);
    }

    private async Task<(bool Ok, string Body)> SendAsync(
        HttpMethod method, string relativePath, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_base, relativePath)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, body);
    }

    private static IReadOnlyList<string> ReadLinks(JsonElement root)
    {
        if (!root.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (var link in links.EnumerateArray())
            if (link.ValueKind == JsonValueKind.String && link.GetString() is { } s)
                result.Add(s);
        return result;
    }

    private static DebridLink? ToRawLink(string link)
        => Uri.TryCreate(link, UriKind.Absolute, out var uri) ? new DebridLink(FileNameOf(uri), uri, null) : null;

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
        => new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static string FileNameOf(Uri uri) => Uri.UnescapeDataString(uri.Segments[^1]);

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static string Summarize(string body)
        => string.IsNullOrWhiteSpace(body) ? "(empty response)" : body.Length > 200 ? body[..200] : body;

    private static Uri EnsureTrailingSlash(Uri uri)
        => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
