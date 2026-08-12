using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EverythingBox.Server.Abstractions;
using Microsoft.AspNetCore.Http;

namespace EverythingBox.Server.Subsonic;

/// <summary>Wraps a payload node in the <c>&lt;subsonic-response&gt;</c> envelope and negotiates the
/// wire format from the <c>?f=</c> query parameter: <c>json</c>/<c>jsonp</c> render JSON (jsonp is
/// wrapped in the <c>?callback=</c> function call), anything else renders XML (the Subsonic default).
/// Both formats come from the SAME <see cref="SubsonicNode"/> model.</summary>
public static partial class SubsonicResponse
{
    public const string ApiVersion = "1.16.1";
    public const string ServerType = "EverythingBox";

    // A JSONP callback is reflected UNESCAPED into a text/javascript body, so it must be a bare JS
    // identifier / dotted path (e.g. angular.callbacks._0) — never anything that could inject script.
    [GeneratedRegex(@"^[A-Za-z0-9_.$]+$")]
    private static partial Regex CallbackPattern();

    public static IResult Ok(HttpRequest req, SubsonicNode? payload) => Render(req, "ok", payload);

    public static IResult Error(HttpRequest req, int code, string message)
        => Render(req, "failed", new SubsonicNode("error").Attr("code", code.ToString()).Attr("message", message));

    private static IResult Render(HttpRequest req, string status, SubsonicNode? payload)
    {
        var root = new SubsonicNode("subsonic-response")
            .Attr("status", status)
            .Attr("version", ApiVersion)
            .Attr("type", ServerType)
            .Attr("serverVersion", ServerApi.VersionString);
        if (payload is not null) root.Add(payload);

        var format = SubsonicParams.Get(req, "f").ToString();
        var isJsonp = format.Equals("jsonp", StringComparison.OrdinalIgnoreCase);
        var wantJson = isJsonp || format.Equals("json", StringComparison.OrdinalIgnoreCase);

        if (wantJson)
        {
            // Subsonic JSON nests the envelope under the single top-level "subsonic-response" key.
            var top = new Dictionary<string, object?> { ["subsonic-response"] = SubsonicNode.ToJson(root) };
            var json = JsonSerializer.Serialize(top);

            // Never let a browser sniff the JSON/JSONP body as HTML — defends the reflected-callback surface.
            req.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";

            if (isJsonp)
            {
                var callback = SubsonicParams.Get(req, "callback").ToString();
                if (string.IsNullOrEmpty(callback)) callback = "callback";
                // Reflect the callback ONLY when it is a safe identifier; otherwise fall through to plain
                // JSON rather than echo attacker-controlled text into an executable body.
                if (CallbackPattern().IsMatch(callback))
                    return Results.Content($"{callback}({json});", "text/javascript");
            }
            return Results.Content(json, "application/json");
        }

        // XML default. No xmlns/declaration — a single element, attributes in declaration order.
        var xml = SubsonicNode.ToXml(root).ToString(SaveOptions.DisableFormatting);
        return Results.Content(xml, "application/xml");
    }
}
