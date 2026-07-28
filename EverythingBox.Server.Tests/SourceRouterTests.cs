using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;

namespace EverythingBox.Server.Tests;

public class SourceRouterTests
{
    private static SourceRouter Router() => new([new FakeSource("alpha"), new FakeSource("beta")]);

    [Fact]
    public void Routes_to_the_owning_source()
    {
        Assert.True(Router().TryResolve("beta:xyz", out var source, out var payload));
        Assert.Equal("beta", source.Key);
        Assert.Equal("xyz", payload);
    }

    [Fact]
    public void Keeps_colons_inside_the_payload()
    {
        // Payloads are opaque — base64url, JSON, whatever the source likes.
        Assert.True(Router().TryResolve("alpha:a:b:c", out _, out var payload));
        Assert.Equal("a:b:c", payload);
    }

    [Fact]
    public void Matches_the_key_case_insensitively()
    {
        Assert.True(Router().TryResolve("ALPHA:x", out var source, out _));
        Assert.Equal("alpha", source.Key);
    }

    [Theory]
    [InlineData("unknown:x")]   // no such source
    [InlineData("alpha")]       // no separator
    [InlineData(":x")]          // empty key
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_anything_it_cannot_route(string? id)
    {
        Assert.False(Router().TryResolve(id, out _, out _));
    }

    [Fact]
    public void Allows_an_empty_payload()
    {
        // A source may legitimately have a single catalog addressed by key alone.
        Assert.True(Router().TryResolve("alpha:", out _, out var payload));
        Assert.Equal("", payload);
    }

    [Fact]
    public void Prefix_round_trips()
    {
        var id = SourceRouter.Prefix("alpha", "a:b");
        Assert.True(Router().TryResolve(id, out var source, out var payload));
        Assert.Equal("alpha", source.Key);
        Assert.Equal("a:b", payload);
    }
}
