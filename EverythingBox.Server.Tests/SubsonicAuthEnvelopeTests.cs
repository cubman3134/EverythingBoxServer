using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace EverythingBox.Server.Tests;

/// <summary>Integration tests over the Subsonic <c>/rest</c> surface, driving the real host booted with
/// Subsonic enabled and the musiclib plugin loaded via <see cref="SubsonicServerFactory"/>. Covers the
/// per-request auth scheme (t/s token, legacy p / enc:p) against the access token, the dual-format
/// (XML default / f=json) envelope negotiation from the one node model, ping/getLicense, and that a
/// Subsonic-disabled host exposes no /rest route at all.</summary>
[Collection(SubsonicServerCollection.Name)]
public class SubsonicAuthEnvelopeTests
{
    private readonly SubsonicServerFactory _factory;
    private readonly SubsonicDisabledServerFactory _disabled;

    public SubsonicAuthEnvelopeTests(SubsonicServerFactory factory, SubsonicDisabledServerFactory disabled)
    {
        _factory = factory;
        _disabled = disabled;
    }

    private static string Md5Hex(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string TokenAuth(string salt) => $"u=admin&t={Md5Hex(SubsonicServerFactory.Token + salt)}&s={salt}";

    // ---- auth ----

    [Fact]
    public async Task Valid_token_salt_gets_an_ok_envelope()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/ping.view?{TokenAuth("abc123")}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"ok\"", body);
    }

    [Fact]
    public async Task A_wrong_token_is_rejected_as_failed_code_40()
    {
        var resp = await _factory.CreateClient().GetAsync("/rest/ping.view?u=admin&t=deadbeef&s=abc123");
        // Subsonic reports auth failures inside an envelope (HTTP 200), not as a transport error.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"failed\"", body);
        Assert.Contains("code=\"40\"", body);
    }

    [Fact]
    public async Task Legacy_plain_password_is_accepted()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/ping.view?u=admin&p={SubsonicServerFactory.Token}");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"ok\"", body);
    }

    [Fact]
    public async Task Legacy_enc_hex_password_is_accepted()
    {
        var enc = "enc:" + Convert.ToHexString(Encoding.UTF8.GetBytes(SubsonicServerFactory.Token)).ToLowerInvariant();
        var resp = await _factory.CreateClient().GetAsync($"/rest/ping.view?u=admin&p={enc}");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"ok\"", body);
    }

    [Fact]
    public async Task No_credentials_against_a_tokened_host_fail()
    {
        var resp = await _factory.CreateClient().GetAsync("/rest/ping.view");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"failed\"", body);
        Assert.Contains("code=\"40\"", body);
    }

    // ---- envelope negotiation ----

    [Fact]
    public async Task Default_format_is_xml()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/ping.view?{TokenAuth("saltx")}");
        Assert.Contains("application/xml", resp.Content.Headers.ContentType!.ToString());
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<subsonic-response status=\"ok\" version=\"1.16.1\" type=\"EverythingBox\"", body);
    }

    [Fact]
    public async Task Json_format_nests_under_the_subsonic_response_key()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/ping.view?f=json&{TokenAuth("saltj")}");
        Assert.Contains("application/json", resp.Content.Headers.ContentType!.ToString());
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"subsonic-response\"", body);
        Assert.Contains("\"status\":\"ok\"", body);
        Assert.Contains("\"version\":\"1.16.1\"", body);
        Assert.Contains("\"type\":\"EverythingBox\"", body);
    }

    [Fact]
    public async Task GetLicense_reports_valid_true_in_xml()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/getLicense.view?{TokenAuth("saltl")}");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("<license valid=\"true\"", body);
    }

    [Fact]
    public async Task GetLicense_reports_valid_true_in_json()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/getLicense.view?f=json&{TokenAuth("saltlj")}");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"license\":{\"valid\":\"true\"}", body);
    }

    [Fact]
    public async Task An_endpoint_without_the_dot_view_suffix_also_routes()
    {
        var resp = await _factory.CreateClient().GetAsync($"/rest/ping?{TokenAuth("saltp")}");
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"ok\"", body);
    }

    // ---- disabled host ----

    [Fact]
    public async Task A_subsonic_disabled_host_has_no_rest_route()
    {
        var resp = await _disabled.CreateClient().GetAsync("/rest/ping.view");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("subsonic-response", body);
    }
}
