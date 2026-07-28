using EverythingBox.Server;

namespace EverythingBox.Server.Tests;

public class SafeUrlGuardTests
{
    [Theory]
    [InlineData("https://example.test/a.mkv")]
    [InlineData("http://192.168.1.10:7000/a.mkv")]
    [InlineData("files/generated.cbz")]          // relative addon path
    [InlineData("proxy/good/abc/name.7z")]
    public void Allows_playable_urls(string url) => Assert.True(SafeUrlGuard.IsClientSafe(url));

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("file:///C:/secrets/passwords.txt")]
    [InlineData("ftp://example.test/a.mkv")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Refuses_everything_else(string? url) => Assert.False(SafeUrlGuard.IsClientSafe(url));
}
