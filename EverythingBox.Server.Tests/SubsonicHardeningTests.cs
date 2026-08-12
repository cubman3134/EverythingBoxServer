using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EverythingBox.Server.Tests;

/// <summary>Hardening of the Subsonic <c>/rest</c> surface for real clients: credentials never reach the
/// request log (the C1 leak — /rest carries them in the QUERY), form-encoded POST authenticates like a
/// GET, a malicious JSONP callback is not reflected, and a throwing plugin library is contained in a
/// code-0 envelope rather than a raw 500.</summary>
[Collection(SubsonicServerCollection.Name)]
public class SubsonicHardeningTests
{
    private readonly SubsonicServerFactory _factory;
    private readonly SubsonicThrowingServerFactory _throwing;

    public SubsonicHardeningTests(SubsonicServerFactory factory, SubsonicThrowingServerFactory throwing)
    {
        _factory = factory;
        _throwing = throwing;
    }

    private static string Md5Hex(string s)
        => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ---- C1: credentials never reach the log ----

    [Fact]
    public async Task Subsonic_credentials_are_redacted_from_the_request_log()
    {
        _factory.LoggedMessages.Clear();

        // A /rest request carrying every credential-bearing param in the query. Auth fails (the values are
        // bogus) but the middleware still logs the line — and it must not leak the query.
        await _factory.CreateClient().GetAsync("/rest/ping?u=x&p=secret&t=abc&s=salt");

        // The whole /rest query is blanked, so a request line is present but names no credential value.
        Assert.Contains(_factory.LoggedMessages, m => m.Contains("/rest/ping", StringComparison.Ordinal));
        Assert.Contains(_factory.LoggedMessages, m => m.Contains("<redacted>", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.LoggedMessages, m => m.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_factory.LoggedMessages, m => m.Contains("abc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_factory.LoggedMessages, m => m.Contains("salt", StringComparison.OrdinalIgnoreCase));
    }

    // ---- I1: a form-encoded POST authenticates like a GET ----

    [Fact]
    public async Task A_form_encoded_POST_with_valid_creds_authenticates()
    {
        const string salt = "postsalt";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["u"] = "admin",
            ["t"] = Md5Hex(SubsonicServerFactory.Token + salt),
            ["s"] = salt,
        });

        var resp = await _factory.CreateClient().PostAsync("/rest/ping.view", form);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"ok\"", body);
    }

    // ---- I2: a malicious JSONP callback is not reflected ----

    [Fact]
    public async Task A_malicious_jsonp_callback_is_not_reflected()
    {
        const string salt = "jsonpsalt";
        var auth = $"u=admin&t={Md5Hex(SubsonicServerFactory.Token + salt)}&s={salt}";
        // A callback with '<' and '(' must be rejected — it would otherwise inject script into the body.
        var evil = Uri.EscapeDataString("evil<script>(");

        var resp = await _factory.CreateClient().GetAsync($"/rest/ping.view?f=jsonp&callback={evil}&{auth}");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.DoesNotContain("evil<script>", body);
        Assert.DoesNotContain("evil<script>(", body);
        // Falls back to plain JSON (parses cleanly), and is marked nosniff so no browser sniffs it as HTML.
        using var _ = JsonDocument.Parse(body);
        Assert.True(resp.Headers.TryGetValues("X-Content-Type-Options", out var v) && v.Contains("nosniff"));
    }

    // ---- I4: a throwing plugin library is contained ----

    [Fact]
    public async Task A_throwing_library_is_contained_in_a_code_0_envelope_not_a_500()
    {
        const string salt = "throwsalt";
        var auth = $"u=admin&t={Md5Hex(SubsonicThrowingServerFactory.Token + salt)}&s={salt}";

        var resp = await _throwing.CreateClient().GetAsync($"/rest/getArtists.view?{auth}");

        // Contained: HTTP 200 with a Subsonic failure envelope, never a raw 500.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("status=\"failed\"", body);
        Assert.Contains("code=\"0\"", body);
    }
}
