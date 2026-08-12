using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EverythingBox.Server.Tests;

/// <summary>Integration tests over the Subsonic Increment-4 Task-2 WRITE endpoints — star / unstar /
/// getStarred2 (stars as DATETIMES) and scrobble (record-only) — driving the real host booted with
/// Subsonic enabled and the musiclib plugin loaded via <see cref="SubsonicServerFactory"/> over its
/// synthesized tagged library. Every request is authenticated with a valid t/s token. A star records the
/// instant it was set; getSong then carries a <c>starred="&lt;ISO8601&gt;"</c> attribute (XML) /
/// <c>"starred":"&lt;iso&gt;"</c> (JSON) and getStarred2 lists the item; unstar removes both. scrobble is
/// record-only (no external forwarding — the #192 boundary).</summary>
[Collection(SubsonicServerCollection.Name)]
public class SubsonicStarScrobbleTests
{
    private readonly SubsonicServerFactory _factory;

    public SubsonicStarScrobbleTests(SubsonicServerFactory factory) => _factory = factory;

    private static string Md5Hex(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string Auth()
    {
        var salt = Guid.NewGuid().ToString("N")[..8];
        return $"u=admin&t={Md5Hex(SubsonicServerFactory.Token + salt)}&s={salt}";
    }

    private static string Attr(XElement el, string name) => el.Attribute(name)?.Value ?? "";

    private async Task<XElement> XmlAsync(string endpointAndQuery)
    {
        var url = $"/rest/{endpointAndQuery}&{Auth()}";
        var resp = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return XElement.Parse(await resp.Content.ReadAsStringAsync());
    }

    // Drill getArtists → getArtist → getAlbum to a real, stable song id (Nova's "Nova Theme").
    private async Task<string> ASongId()
    {
        var artists = await XmlAsync("getArtists?");
        var artistId = Attr(artists.Descendants("artist").Single(a => Attr(a, "name") == "Nova"), "id");
        var artist = await XmlAsync($"getArtist?id={artistId}");
        var albumId = Attr(artist.Descendants("album").Single(a => Attr(a, "name") == "Nova Nights"), "id");
        var album = await XmlAsync($"getAlbum?id={albumId}");
        return Attr(album.Descendants("song").First(a => Attr(a, "title") == "Nova Theme"), "id");
    }

    private async Task<string> StarAsync(string id) =>
        await (await _factory.CreateClient().GetAsync($"/rest/star?id={id}&{Auth()}")).Content.ReadAsStringAsync();

    private async Task<string> UnstarAsync(string id) =>
        await (await _factory.CreateClient().GetAsync($"/rest/unstar?id={id}&{Auth()}")).Content.ReadAsStringAsync();

    private static void AssertOk(string body)
        => Assert.Equal("ok", Attr(XElement.Parse(body), "status"));

    private static void AssertFailed(string body, string code)
    {
        var root = XElement.Parse(body);
        Assert.Equal("failed", Attr(root, "status"));
        Assert.Equal(code, Attr(root.Element("error")!, "code"));
    }

    // A well-formed Subsonic ISO instant: yyyy-MM-ddTHH:mm:ss.fffZ.
    private static bool IsSubsonicIso(string s)
        => DateTimeOffset.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out _);

    [Fact]
    public async Task Star_marks_the_song_with_a_datetime_shown_in_getSong_and_listed_by_getStarred2()
    {
        var songId = await ASongId();
        try
        {
            AssertOk(await StarAsync(songId));

            // getSong (XML): the song node now carries starred="<iso datetime>".
            var song = (await XmlAsync($"getSong?id={songId}")).Element("song")!;
            var starred = Attr(song, "starred");
            Assert.NotEmpty(starred);
            Assert.True(IsSubsonicIso(starred), $"starred was not a Subsonic ISO instant: '{starred}'");

            // getSong (JSON): "starred":"<iso>" (a quoted string, not a bool).
            var jsonResp = await _factory.CreateClient().GetAsync($"/rest/getSong?id={songId}&f=json&{Auth()}");
            using var doc = JsonDocument.Parse(await jsonResp.Content.ReadAsStringAsync());
            var jsonStarred = doc.RootElement
                .GetProperty("subsonic-response").GetProperty("song").GetProperty("starred");
            Assert.Equal(JsonValueKind.String, jsonStarred.ValueKind);
            Assert.True(IsSubsonicIso(jsonStarred.GetString()!));

            // getStarred2 lists the song.
            var starred2 = await XmlAsync("getStarred2?");
            Assert.Contains(starred2.Descendants("song"), s => Attr(s, "id") == songId);
        }
        finally { await UnstarAsync(songId); }
    }

    [Fact]
    public async Task Unstar_removes_the_attribute_and_drops_the_song_from_getStarred2()
    {
        var songId = await ASongId();
        AssertOk(await StarAsync(songId));
        AssertOk(await UnstarAsync(songId));

        // The starred attribute is GONE (omitted, never starred="false").
        var song = (await XmlAsync($"getSong?id={songId}")).Element("song")!;
        Assert.Null(song.Attribute("starred"));

        // And getStarred2 no longer lists it.
        var starred2 = await XmlAsync("getStarred2?");
        Assert.DoesNotContain(starred2.Descendants("song"), s => Attr(s, "id") == songId);
    }

    [Fact]
    public async Task Star_persists_server_side_and_is_seen_by_a_fresh_client()
    {
        var songId = await ASongId();
        try
        {
            AssertOk(await StarAsync(songId));

            // A brand-new client (fresh request) still sees the star — it lives in the server-side store,
            // not in any per-request state. (The store's true on-disk reload is covered at the unit level
            // by MusicLibraryImplTests.SetStarred_is_reflected_and_survives_a_reload.)
            var starred2 = await XmlAsync("getStarred2?");
            Assert.Contains(starred2.Descendants("song"), s => Attr(s, "id") == songId);
        }
        finally { await UnstarAsync(songId); }
    }

    [Fact]
    public async Task Scrobble_returns_ok_and_is_safe_to_repeat()
    {
        var songId = await ASongId();

        AssertOk(await (await _factory.CreateClient().GetAsync($"/rest/scrobble?id={songId}&{Auth()}")).Content.ReadAsStringAsync());
        // Repeat (with an explicit ms-epoch time) — record-only, must not throw or fail.
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AssertOk(await (await _factory.CreateClient().GetAsync($"/rest/scrobble?id={songId}&time={time}&{Auth()}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Scrobble_without_id_fails_with_code_10()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/scrobble?{Auth()}");
        AssertFailed(await resp.Content.ReadAsStringAsync(), "10");
    }

    [Fact]
    public async Task Star_of_an_unknown_id_is_tolerated_and_corrupts_no_state()
    {
        const string unknown = "star-of-a-ghost-id";
        try
        {
            // Subsonic tolerates starring an unknown id — an ok envelope, not an error.
            AssertOk(await StarAsync(unknown));

            // getStarred2 still works and never surfaces the unresolvable id as a song (Starred() walks the
            // real index), so nothing is corrupted.
            var starred2 = await XmlAsync("getStarred2?");
            Assert.Equal("ok", Attr(starred2, "status"));
            Assert.DoesNotContain(starred2.Descendants("song"), s => Attr(s, "id") == unknown);
        }
        finally { await UnstarAsync(unknown); }
    }
}
