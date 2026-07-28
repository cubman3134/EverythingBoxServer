namespace EverythingBox.Server.Abstractions;

/// <summary>
/// Supplies the cookies (and the matching User-Agent) a feed URL needs — typically a
/// Cloudflare <c>cf_clearance</c> read from the user's own browser, so a feed behind a
/// challenge can be fetched with the same credentials the browser already obtained.
/// A <c>cf_clearance</c> is bound to both the User-Agent and the domain, so this is
/// resolved per request.
/// </summary>
public interface IFeedCookieSource
{
    /// <summary>Cookie header + matching User-Agent for the URL's host, or null if none.</summary>
    BrowserCredentials? For(Uri url);
}

/// <summary>A <c>Cookie</c> header value and the User-Agent it is tied to.</summary>
public sealed record BrowserCredentials(string Cookie, string? UserAgent);
