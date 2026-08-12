using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace EverythingBox.Server.Subsonic;

public static class SubsonicAuth
{
    /// <summary>True when the request is authorised. Empty accessToken ⇒ open (LAN). Supports the token
    /// scheme <c>t=md5(password+salt),s=salt</c> and the legacy <c>p=password</c> (plain or
    /// <c>"enc:"+hex</c>), where the password is the server access token. Never logs any credential.</summary>
    public static bool Authenticate(HttpRequest req, string? accessToken)
    {
        var token = accessToken?.Trim();
        if (string.IsNullOrEmpty(token)) return true;                    // tokenless LAN = open

        var t = req.Query["t"].ToString();
        var s = req.Query["s"].ToString();
        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(s))
        {
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(token + s))).ToLowerInvariant();
            return string.Equals(t, expected, StringComparison.OrdinalIgnoreCase);
        }

        var p = req.Query["p"].ToString();
        if (!string.IsNullOrEmpty(p))
        {
            if (p.StartsWith("enc:", StringComparison.Ordinal))
            {
                try { p = Encoding.UTF8.GetString(Convert.FromHexString(p[4..])); } catch { return false; }
            }
            return string.Equals(p, token, StringComparison.Ordinal);
        }

        return false;
    }
}
