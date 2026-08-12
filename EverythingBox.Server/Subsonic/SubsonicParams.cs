using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace EverythingBox.Server.Subsonic;

/// <summary>Reads a Subsonic request parameter from the query string, falling back to a form-encoded POST
/// body. The <c>/rest</c> surface maps BOTH GET and POST, and Subsonic clients may send credentials and
/// params either on the query (GET) or as <c>application/x-www-form-urlencoded</c> (POST). Every param
/// read — auth AND the endpoints — goes through here so a form POST authenticates and dispatches exactly
/// like the equivalent GET; reading only <see cref="HttpRequest.Query"/> silently failed every form POST.</summary>
internal static class SubsonicParams
{
    public static StringValues Get(HttpRequest req, string key)
    {
        var v = req.Query[key];
        // HasFormContentType gates the Form read; urlencoded forms are buffered, so req.Form is a
        // synchronous read off an already-materialised body (no async needed on this path).
        if (StringValues.IsNullOrEmpty(v) && req.HasFormContentType)
            v = req.Form[key];
        return v;
    }
}
