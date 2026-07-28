using EverythingBox.Server.Abstractions;
using EverythingBox.Server.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverythingBox.Server.Tests;

/// <summary>Captures everything logged, so a test can assert a specific error fired
/// without depending on any particular logging provider.</summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
}

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

    // F3: two independently-authored plugins can plausibly register the same source key.
    // Before the fix, the constructor's plain ToDictionary threw ArgumentException on a
    // duplicate — and because Program.cs resolves SourceRouter eagerly, that refused to
    // start the whole server. It must instead keep the first registration and drop the
    // second, the same way every other plugin failure mode is contained.

    [Fact]
    public void Two_sources_with_the_same_key_do_not_crash_the_constructor()
    {
        var first = new FakeSource("dup");
        var second = new FakeSource("dup");

        var router = new SourceRouter([first, second]);

        Assert.Single(router.Sources);
    }

    [Fact]
    public void The_first_registration_of_a_duplicate_key_is_kept()
    {
        var first = new FakeSource("dup", [new CatalogDescriptor("first-cat", "First", "movie")]);
        var second = new FakeSource("dup", [new CatalogDescriptor("second-cat", "Second", "movie")]);

        var router = new SourceRouter([first, second]);

        Assert.True(router.TryResolve("dup:x", out var source, out _));
        Assert.Same(first, source);
    }

    [Fact]
    public void A_duplicate_key_logs_an_error_naming_the_key()
    {
        var log = new CapturingLogger<SourceRouter>();

        _ = new SourceRouter([new FakeSource("dup"), new FakeSource("dup")], log);

        Assert.Contains(log.Messages, m => m.Contains("dup", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_key_check_is_case_insensitive()
    {
        var first = new FakeSource("dup");
        var second = new FakeSource("DUP");

        var router = new SourceRouter([first, second]);

        Assert.Single(router.Sources);
        Assert.True(router.TryResolve("dup:x", out var source, out _));
        Assert.Same(first, source);
    }
}
