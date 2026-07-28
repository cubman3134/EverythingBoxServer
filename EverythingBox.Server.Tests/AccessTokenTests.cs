using System.Net;

namespace EverythingBox.Server.Tests;

/// <summary>F4: the access token is the server's only authentication mechanism, and had no
/// test coverage at all. These drive the real host with a token configured.</summary>
[Collection(TokenServerCollection.Name)]
public class AccessTokenTests
{
    private readonly TokenPluginServerFactory _factory;
    public AccessTokenTests(TokenPluginServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Manifest_succeeds_under_the_token_prefixed_path()
    {
        var response = await _factory.CreateClient().GetAsync($"/{TokenPluginServerFactory.Token}/manifest.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_without_the_token_prefix_does_not_route()
    {
        var response = await _factory.CreateClient().GetAsync("/manifest.json");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ASP.NET's routing matches literal route segments case-insensitively, so a request whose
    // token casing differs from what's configured still reaches the route — the log redaction
    // (Program.cs's request-logging middleware) has to match that, or a differently-cased
    // request leaks the token into the log in plaintext.
    [Fact]
    public async Task A_differently_cased_token_still_routes_and_is_still_redacted_from_the_log()
    {
        _factory.LoggedMessages.Clear();
        var differentlyCased = TokenPluginServerFactory.Token.ToUpperInvariant();

        var response = await _factory.CreateClient().GetAsync($"/{differentlyCased}/manifest.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.LoggedMessages, m => m.Contains("/<token>/manifest.json", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.LoggedMessages,
            m => m.Contains(TokenPluginServerFactory.Token, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_token_is_redacted_from_the_log_at_its_configured_casing_too()
    {
        _factory.LoggedMessages.Clear();

        await _factory.CreateClient().GetAsync($"/{TokenPluginServerFactory.Token}/manifest.json");

        Assert.Contains(_factory.LoggedMessages, m => m.Contains("/<token>/manifest.json", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.LoggedMessages,
            m => m.Contains(TokenPluginServerFactory.Token, StringComparison.OrdinalIgnoreCase));
    }
}
