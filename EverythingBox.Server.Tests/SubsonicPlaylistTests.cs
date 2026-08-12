using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EverythingBox.Server.Tests;

/// <summary>Integration tests over the Subsonic playlist READ endpoints — getPlaylists / getPlaylist —
/// driving the real host booted with Subsonic enabled and the musiclib plugin loaded via
/// <see cref="SubsonicServerFactory"/>. The factory seeds one playlist ("Road Trip") into the plugin's
/// local store before boot (there is no Subsonic create verb); getPlaylists must list it and
/// getPlaylist?id=… must expand it into its member songs as &lt;entry&gt; nodes. Every request carries a
/// valid t/s token.</summary>
[Collection(SubsonicServerCollection.Name)]
public class SubsonicPlaylistTests
{
    private readonly SubsonicServerFactory _factory;

    public SubsonicPlaylistTests(SubsonicServerFactory factory) => _factory = factory;

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

    [Fact]
    public async Task GetPlaylists_lists_the_seeded_playlist_with_its_counts()
    {
        var root = await XmlAsync("getPlaylists?");
        Assert.Equal("ok", Attr(root, "status"));

        var pl = Assert.Single(
            root.Descendants("playlist"),
            p => Attr(p, "id") == SubsonicServerFactory.SeededPlaylistId);
        Assert.Equal(SubsonicServerFactory.SeededPlaylistName, Attr(pl, "name"));
        Assert.Equal("1", Attr(pl, "songCount"));       // its single member resolves to a real song
        Assert.NotEmpty(Attr(pl, "duration"));          // numeric duration present
    }

    [Fact]
    public async Task GetPlaylist_expands_the_playlist_into_its_entry_songs_xml()
    {
        var root = await XmlAsync($"getPlaylist?id={SubsonicServerFactory.SeededPlaylistId}");
        var pl = root.Element("playlist")!;
        Assert.Equal(SubsonicServerFactory.SeededPlaylistId, Attr(pl, "id"));
        Assert.Equal(SubsonicServerFactory.SeededPlaylistName, Attr(pl, "name"));

        // Members are <entry> nodes (Subsonic's element name for a playlist song), same attributes as
        // <song>: the seeded member is Nova's "Nova Theme", carrying the encoded-path id the factory seeded.
        var entry = Assert.Single(pl.Elements("entry"));
        Assert.Equal(_factory.SeededSongId, Attr(entry, "id"));
        Assert.Equal("Nova Theme", Attr(entry, "title"));
    }

    [Fact]
    public async Task GetPlaylist_json_carries_the_playlist_and_its_entry()
    {
        var resp = await _factory.CreateClient()
            .GetAsync($"/rest/getPlaylist?id={SubsonicServerFactory.SeededPlaylistId}&f=json&{Auth()}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pl = doc.RootElement.GetProperty("subsonic-response").GetProperty("playlist");
        Assert.Equal(SubsonicServerFactory.SeededPlaylistId, pl.GetProperty("id").GetString());
        // A single member collapses to one entry object; songCount is a native JSON number.
        Assert.Equal(1, pl.GetProperty("songCount").GetInt32());
        var entry = pl.GetProperty("entry");
        Assert.Equal(_factory.SeededSongId, entry.GetProperty("id").GetString());
        Assert.Equal("Nova Theme", entry.GetProperty("title").GetString());
    }

    [Fact]
    public async Task GetPlaylist_with_an_unknown_id_fails_with_code_70()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/getPlaylist?id=no-such-playlist&{Auth()}");
        AssertFailed(await resp.Content.ReadAsStringAsync(), "70");
    }

    [Fact]
    public async Task GetPlaylist_with_no_id_fails_with_code_10()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/getPlaylist?{Auth()}");
        AssertFailed(await resp.Content.ReadAsStringAsync(), "10");
    }

    private static void AssertFailed(string body, string code)
    {
        var root = XElement.Parse(body);
        Assert.Equal("failed", Attr(root, "status"));
        Assert.Equal(code, Attr(root.Element("error")!, "code"));
    }
}
