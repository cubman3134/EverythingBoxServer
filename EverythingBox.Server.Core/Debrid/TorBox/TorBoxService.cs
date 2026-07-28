using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using EverythingBox.Server.Abstractions;

namespace EverythingBox.Server.Core.Debrid.TorBox;

/// <summary>
/// <see cref="IDebridService"/> for TorBox. Drives its flow: <c>createtorrent</c>
/// (magnet) → poll <c>torrents/mylist</c> until the torrent is present → request a
/// direct link per file via <c>torrents/requestdl</c>. Cached torrents resolve in
/// one pass; uncached ones return <see cref="DebridStatus.Pending"/> unless
/// <see cref="TorBoxOptions.MaxWait"/> allows waiting.
/// </summary>
public sealed class TorBoxService : IDebridService, ICachedAvailabilityChecker, IDebridLibrary
{
    private readonly HttpClient _http;
    private readonly TorBoxOptions _options;
    private readonly Uri _base;

    public TorBoxService(HttpClient http, TorBoxOptions options)
    {
        _http = http;
        _options = options;
        _base = EnsureTrailingSlash(options.BaseUrl);
    }

    public string Name => _options.Name;

    // TorBox downloads the whole torrent (no per-file selection), so the request
    // is accepted for interface parity but not used to pre-select files; callers
    // narrow the returned links with MediaFileMatcher instead.
    public async Task<DebridResult> ResolveAsync(
        TorrentResult torrent, MediaRequest? request = null, CancellationToken cancellationToken = default)
    {
        var magnet = await MagnetResolver.ResolveAsync(_http, torrent, cancellationToken).ConfigureAwait(false);
        if (magnet is null)
            return DebridResult.Failed(Name, "release has no magnet, info hash, or .torrent file");

        try
        {
            // 1. Add the torrent (TorBox dedupes by hash, so this is idempotent).
            using var form = new MultipartFormDataContent { { new StringContent(magnet), "magnet" } };
            var (createOk, createBody) = await SendAsync(HttpMethod.Post, "torrents/createtorrent", form, cancellationToken)
                .ConfigureAwait(false);
            if (!createOk)
                return DebridResult.Failed(Name, $"createtorrent: {Summarize(createBody)}");

            string? id;
            using (var doc = JsonDocument.Parse(createBody))
            {
                if (!IsSuccess(doc.RootElement))
                    return DebridResult.Failed(Name, Detail(doc.RootElement) ?? "createtorrent failed");
                id = TorrentId(doc.RootElement);
            }
            if (string.IsNullOrEmpty(id))
                return DebridResult.Failed(Name, "createtorrent returned no torrent id");

            // 2. Poll until the torrent is present on TorBox.
            var stopwatch = Stopwatch.StartNew();
            var attempt = 0;
            while (true)
            {
                var (listOk, listBody) = await SendAsync(HttpMethod.Get, $"torrents/mylist?id={id}&bypass_cache=true", null, cancellationToken)
                    .ConfigureAwait(false);
                if (!listOk)
                    return DebridResult.Failed(Name, $"mylist: {Summarize(listBody)}", id);

                bool ready;
                List<(int FileId, string Name, long? Size)> files;
                using (var doc = JsonDocument.Parse(listBody))
                {
                    var data = DataObject(doc.RootElement);
                    ready = data is { } d && (Bool(d, "download_finished") || Bool(d, "download_present"));
                    files = data is { } d2 ? ReadFiles(d2) : [];
                }

                if (ready)
                    return await RequestLinksAsync(id, cached: attempt == 0, files, cancellationToken).ConfigureAwait(false);

                if (stopwatch.Elapsed >= _options.MaxWait)
                    return DebridResult.Pending(Name, id, "not cached yet");

                attempt++;
                await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return DebridResult.Failed(Name, "could not parse TorBox response");
        }
    }

    public async Task<IReadOnlySet<string>> GetCachedHashesAsync(
        IEnumerable<string> infoHashes,
        CancellationToken cancellationToken = default)
    {
        var hashes = infoHashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count == 0)
            return cached;

        var query = $"torrents/checkcached?hash={string.Join(',', hashes)}&format=object&list_files=false";
        var (ok, body) = await SendAsync(HttpMethod.Get, query, null, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return cached;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsSuccess(root) || !root.TryGetProperty("data", out var data))
                return cached;

            // format=object: keys are the cached hashes. (Some responses also
            // carry the hash inside each value; read both to be safe.)
            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in data.EnumerateObject())
                {
                    cached.Add(entry.Name);
                    if (entry.Value.ValueKind == JsonValueKind.Object
                        && entry.Value.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String)
                        cached.Add(h.GetString()!);
                }
            }
            else if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                    if (item.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String)
                        cached.Add(h.GetString()!);
            }
        }
        catch (JsonException)
        {
            // treat as none cached
        }

        return cached;
    }

    public async Task<IReadOnlyList<TorrentResult>> ListLibraryAsync(CancellationToken cancellationToken = default)
    {
        var (ok, body) = await SendAsync(HttpMethod.Get, "torrents/mylist?bypass_cache=true", null, cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
            return [];

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsSuccess(root) || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<TorrentResult>();
            foreach (var item in data.EnumerateArray())
            {
                var hash = GetString(item, "hash");
                var name = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(name))
                    continue;

                long? size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
                if (size is <= 0)
                    size = null; // TorBox reports -1 for unknown

                results.Add(new TorrentResult
                {
                    Title = name,
                    ProviderName = Name,
                    InfoHash = hash,
                    MagnetUri = new Uri($"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}"),
                    SizeBytes = size,
                    Seeders = 1, // already in the account; seeders are irrelevant but keep it rank-eligible
                    Categories = ["Debrid library"],
                });
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<DebridProgress?> GetProgressAsync(string infoHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
            return null;
        var wanted = infoHash.Trim();

        var (ok, body) = await SendAsync(HttpMethod.Get, "torrents/mylist?bypass_cache=true", null, cancellationToken)
            .ConfigureAwait(false);
        if (!ok)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsSuccess(root) || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in data.EnumerateArray())
            {
                if (!string.Equals(GetString(item, "hash"), wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                var progress = item.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0d;
                var state = GetString(item, "download_state");
                int? seeds = item.TryGetProperty("seeds", out var sd) && sd.ValueKind == JsonValueKind.Number ? sd.GetInt32() : null;
                return new DebridProgress(progress, state, seeds);
            }
            return null; // not in the account (yet)
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<DebridResult> RequestLinksAsync(
        string id, bool cached, List<(int FileId, string Name, long? Size)> files, CancellationToken cancellationToken)
    {
        var links = new List<DebridLink>();
        foreach (var file in files)
        {
            var relative = $"torrents/requestdl?token={Uri.EscapeDataString(_options.ApiKey)}&torrent_id={id}&file_id={file.FileId}";
            var (ok, body) = await SendAsync(HttpMethod.Get, relative, null, cancellationToken).ConfigureAwait(false);
            if (!ok)
                continue;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!IsSuccess(root))
                continue;

            if (root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String
                && Uri.TryCreate(data.GetString(), UriKind.Absolute, out var url))
            {
                links.Add(new DebridLink(file.Name, url, file.Size));
            }
        }

        // Multi-file torrent (e.g. a PC game repack: setup.exe + .bin parts): also offer a single zip of the
        // whole torrent, so a caller that needs every file can take one download. Named ".zip" and sized as
        // the total, so a size/extension-based pick can prefer it; per-file pickers (video/book) ignore it.
        if (files.Count > 1)
        {
            var relative = $"torrents/requestdl?token={Uri.EscapeDataString(_options.ApiKey)}&torrent_id={id}&zip_link=true";
            var (ok, body) = await SendAsync(HttpMethod.Get, relative, null, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (IsSuccess(root) && root.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(data.GetString(), UriKind.Absolute, out var url))
                {
                    var first = files[0].Name ?? "release";
                    var slash = first.IndexOfAny(['/', '\\']);
                    var stem = slash > 0 ? first[..slash] : first;
                    long total = 0; foreach (var f in files) total += f.Size ?? 0;
                    links.Insert(0, new DebridLink(stem + ".zip", url, total > 0 ? total : null));
                }
            }
        }

        return DebridResult.Resolved(Name, id, cached, links);
    }

    private async Task<(bool Ok, string Body)> SendAsync(
        HttpMethod method, string relativePath, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_base, relativePath)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, body);
    }

    private static JsonElement? DataObject(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return null;
        if (data.ValueKind == JsonValueKind.Object)
            return data;
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            return data[0];
        return null;
    }

    private static List<(int FileId, string Name, long? Size)> ReadFiles(JsonElement data)
    {
        var files = new List<(int, string, long?)>();
        if (!data.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return files;

        foreach (var f in arr.EnumerateArray())
        {
            var id = f.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.Number ? idv.GetInt32() : 0;
            var name = f.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString()! : $"file-{id}";
            long? size = f.TryGetProperty("size", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt64() : null;
            files.Add((id, name, size));
        }

        return files;
    }

    private static string? TorrentId(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("torrent_id", out var t) && t.ValueKind == JsonValueKind.Number)
            return t.GetInt64().ToString();
        if (data.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number)
            return i.GetInt64().ToString();
        return null;
    }

    private static bool IsSuccess(JsonElement root)
        => root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

    private static bool Bool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Detail(JsonElement root)
    {
        if (root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            return d.GetString();
        if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            return e.GetString();
        return null;
    }

    private static string Summarize(string body)
        => string.IsNullOrWhiteSpace(body) ? "(empty response)" : body.Length > 200 ? body[..200] : body;

    private static Uri EnsureTrailingSlash(Uri uri)
        => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
