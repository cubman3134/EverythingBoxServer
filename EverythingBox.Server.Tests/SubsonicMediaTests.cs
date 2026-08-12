using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace EverythingBox.Server.Tests;

/// <summary>Integration tests over the Subsonic Increment-4 MEDIA endpoints (stream / download /
/// getCoverArt), driving the real host booted with Subsonic enabled and the musiclib plugin loaded via
/// <see cref="SubsonicServerFactory"/> over its synthesized tagged library. Every request is
/// authenticated with a valid t/s token (the same MD5(token+salt) helper the read tests use). These
/// endpoints DIRECT-PLAY: the original bytes are relayed, Range-served, with no transcoding — so a
/// maxBitRate/format request still returns the source bytes unchanged.</summary>
[Collection(SubsonicServerCollection.Name)]
public class SubsonicMediaTests
{
    private readonly SubsonicServerFactory _factory;

    public SubsonicMediaTests(SubsonicServerFactory factory) => _factory = factory;

    private static string Md5Hex(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Auth() =>
        Auth(Guid.NewGuid().ToString("N")[..8]);
    private static string Auth(string salt) =>
        $"u=admin&t={Md5Hex(SubsonicServerFactory.Token + salt)}&s={salt}";

    // A read-endpoint XML fetch, reused to discover a real song id and a real cover-art id.
    private async Task<XElement> XmlAsync(string endpointAndQuery)
    {
        var url = $"/rest/{endpointAndQuery}&{Auth()}";
        var resp = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return XElement.Parse(await resp.Content.ReadAsStringAsync());
    }

    private static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";

    private async Task<string> ArtistIdOf(string name)
    {
        var root = await XmlAsync("getArtists?");
        return Attr(root.Descendants("artist").Single(a => Attr(a, "name") == name), "id");
    }

    private async Task<XElement> NovaNightsAlbum()
    {
        var artistId = await ArtistIdOf("Nova");
        var artist = await XmlAsync($"getArtist?id={artistId}");
        var albumId = Attr(artist.Descendants("album").Single(a => Attr(a, "name") == "Nova Nights"), "id");
        return await XmlAsync($"getAlbum?id={albumId}");
    }

    private async Task<string> ASongIdWithCover()
    {
        var album = await NovaNightsAlbum();
        return Attr(album.Descendants("song").First(), "id");
    }

    private async Task<string> ACoverArtId()
    {
        var album = await NovaNightsAlbum();
        // The album (from a sibling cover.png) carries a coverArt id; its songs inherit the same one.
        var id = Attr(album.Element("album")!, "coverArt");
        Assert.NotEmpty(id);
        return id;
    }

    // ---- stream ----

    [Fact]
    public async Task Stream_serves_the_original_audio_bytes_with_an_audio_content_type()
    {
        var songId = await ASongIdWithCover();
        var resp = await _factory.CreateClient().GetAsync($"/rest/stream?id={songId}&{Auth()}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("audio/", resp.Content.Headers.ContentType!.MediaType!);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task Stream_honors_a_Range_header_with_206_and_a_ten_byte_slice()
    {
        var songId = await ASongIdWithCover();
        var full = await (await _factory.CreateClient().GetAsync($"/rest/stream?id={songId}&{Auth()}"))
            .Content.ReadAsByteArrayAsync();

        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"/rest/stream?id={songId}&{Auth()}");
        req.Headers.Range = new RangeHeaderValue(0, 9);   // bytes=0-9
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.NotNull(resp.Content.Headers.ContentRange);
        var slice = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(10, slice.Length);
        Assert.Equal(full.Take(10), slice);
    }

    [Fact]
    public async Task Stream_ignores_maxBitRate_and_format_and_still_serves_the_original_bytes()
    {
        var songId = await ASongIdWithCover();
        var original = await (await _factory.CreateClient().GetAsync($"/rest/stream?id={songId}&{Auth()}"))
            .Content.ReadAsByteArrayAsync();

        // A hard bitrate cap + a different container: this build does NOT transcode — direct play.
        var resp = await _factory.CreateClient().GetAsync($"/rest/stream?id={songId}&maxBitRate=64&format=mp3&{Auth()}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(original, bytes);   // byte-identical to the source: no re-encode.
    }

    [Fact]
    public async Task Stream_with_no_id_fails_with_code_10()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/stream?{Auth()}");
        AssertFailed(await resp.Content.ReadAsStringAsync(), "10");
    }

    [Fact]
    public async Task Stream_with_an_unknown_id_fails_with_code_70_not_a_bare_404()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/stream?id=song-does-not-exist&{Auth()}");
        // A parseable Subsonic error envelope, not a raw 404.
        AssertFailed(await resp.Content.ReadAsStringAsync(), "70");
    }

    [Fact]
    public async Task Stream_unauthenticated_fails_with_code_40_and_leaks_no_bytes()
    {
        var songId = await ASongIdWithCover();
        var resp = await _factory.CreateClient().GetAsync($"/rest/stream?id={songId}");   // no t/s
        var body = await resp.Content.ReadAsStringAsync();
        AssertFailed(body, "40");
        // The body is the tiny XML envelope, not the audio — no bytes served before auth.
        Assert.DoesNotContain("audio/", resp.Content.Headers.ContentType?.MediaType ?? "");
        Assert.Contains("subsonic-response", body);
    }

    // ---- download ----

    [Fact]
    public async Task Download_serves_the_full_original_bytes()
    {
        var songId = await ASongIdWithCover();
        var streamed = await (await _factory.CreateClient().GetAsync($"/rest/stream?id={songId}&{Auth()}"))
            .Content.ReadAsByteArrayAsync();

        var resp = await _factory.CreateClient().GetAsync($"/rest/download?id={songId}&{Auth()}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
        Assert.Equal(streamed, bytes);   // download is the same original file.
    }

    // ---- getCoverArt ----

    [Fact]
    public async Task GetCoverArt_serves_an_image_and_ignores_size()
    {
        var coverId = await ACoverArtId();

        var resp = await _factory.CreateClient().GetAsync($"/rest/getCoverArt?id={coverId}&{Auth()}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("image/", resp.Content.Headers.ContentType!.MediaType!);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);

        // size is accepted but IGNORED (no resizing in v1) — the same bytes come back.
        var sized = await _factory.CreateClient().GetAsync($"/rest/getCoverArt?id={coverId}&size=100&{Auth()}");
        Assert.Equal(HttpStatusCode.OK, sized.StatusCode);
        Assert.Equal(bytes, await sized.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetCoverArt_with_an_unknown_id_fails_with_code_70()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/getCoverArt?id=cover-does-not-exist&{Auth()}");
        AssertFailed(await resp.Content.ReadAsStringAsync(), "70");
    }

    [Fact]
    public async Task GetCoverArt_with_no_id_fails_with_code_10()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/getCoverArt?{Auth()}");
        AssertFailed(await resp.Content.ReadAsStringAsync(), "10");
    }

    // ---- helpers ----

    private static void AssertFailed(string body, string code)
    {
        var root = XElement.Parse(body);
        Assert.Equal("failed", Attr(root, "status"));
        Assert.Equal(code, Attr(root.Element("error")!, "code"));
    }
}
