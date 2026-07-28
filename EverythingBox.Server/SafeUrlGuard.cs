namespace EverythingBox.Server;

/// <summary>
/// The client must only ever receive something it can play: a relative path this
/// server hosts, or an http(s) URL. A magnet, a local file path, or anything else
/// is refused — including when a plugin returns one by mistake.
/// </summary>
public static class SafeUrlGuard
{
    public static bool IsClientSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Not absolute => a relative path served by this addon.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute)) return true;

        return absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps;
    }
}
