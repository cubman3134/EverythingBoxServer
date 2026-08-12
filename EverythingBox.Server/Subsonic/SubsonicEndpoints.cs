using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Http;

namespace EverythingBox.Server.Subsonic;

public static class SubsonicEndpoints
{
    /// <summary>Maps the Subsonic <c>/rest</c> surface. Deliberately mounted at a BARE <c>/rest</c> with
    /// NO token prefix — unlike every other route on this host, Subsonic authenticates per request
    /// (see <see cref="SubsonicAuth"/>) against the server access token, so it is the first surface the
    /// token does not gate at the URL. Only mapped when Subsonic is enabled AND a music library is
    /// present (see Program.cs).</summary>
    public static void MapSubsonic(this WebApplication app)
    {
        app.MapMethods("/rest/{endpoint}", ["GET", "POST"],
            (string endpoint, HttpContext http, IMusicLibrary music, ServerConfig config) =>
            {
                // Clients hit either /rest/ping or /rest/ping.view — strip a trailing ".view".
                var name = endpoint.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ? endpoint[..^5] : endpoint;

                if (!SubsonicAuth.Authenticate(http.Request, config.AccessToken))
                    return SubsonicResponse.Error(http.Request, 40, "Wrong username or password.");

                return name switch
                {
                    "ping" => SubsonicResponse.Ok(http.Request, null),
                    "getLicense" => SubsonicResponse.Ok(http.Request, new SubsonicNode("license").Attr("valid", "true")),
                    // Read endpoints arrive in Task 2; media streaming in Increment 4.
                    _ => SubsonicResponse.Error(http.Request, 0, $"Endpoint not implemented: {name}"),
                };
            });
    }
}
