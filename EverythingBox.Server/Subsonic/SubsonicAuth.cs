using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace EverythingBox.Server.Subsonic;

public static class SubsonicAuth
{
    /// <summary>True when the request is authorised. Empty accessToken ⇒ open (LAN). Supports the token
    /// scheme <c>t=md5(password+salt),s=salt</c> and the legacy <c>p=password</c> (plain or
    /// <c>"enc:"+hex</c>), where the password is the server access token. Params are read via
    /// <see cref="SubsonicParams"/> so a form-encoded POST authenticates like a GET. Never logs any
    /// credential, and compares in constant time.</summary>
    public static bool Authenticate(HttpRequest req, string? accessToken)
    {
        var token = accessToken?.Trim();
        if (string.IsNullOrEmpty(token)) return true;                    // tokenless LAN = open

        var t = SubsonicParams.Get(req, "t").ToString();
        var s = SubsonicParams.Get(req, "s").ToString();
        if (!string.IsNullOrEmpty(t) && !string.IsNullOrEmpty(s))
        {
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(token + s))).ToLowerInvariant();
            // Constant-time compare: the attacker fixes the salt, so `expected` is stable and a byte-by-byte
            // early-out would be a timing oracle on the hash. Hashes are hex, so normalise case first.
            return FixedTimeEquals(t.ToLowerInvariant(), expected);
        }

        var p = SubsonicParams.Get(req, "p").ToString();
        if (!string.IsNullOrEmpty(p))
        {
            if (p.StartsWith("enc:", StringComparison.Ordinal))
            {
                try { p = Encoding.UTF8.GetString(Convert.FromHexString(p[4..])); } catch { return false; }
            }
            return FixedTimeEquals(p, token);
        }

        return false;
    }

    // Compare over the UTF-8 bytes; FixedTimeEquals is constant-time for equal-length inputs and only
    // leaks length (fixed here — a 32-char hex hash, or the server token).
    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
