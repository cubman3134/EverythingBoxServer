using EverythingBox.Server.Abstractions;

namespace TestPlugin.Throwing;

/// <summary>
/// Loads successfully (unlike the fixtures in TestPlugin.Bad, which are deliberately
/// unloadable) but every member of its one IMediaSource throws once the server is
/// actually driving a request through it. Exists to prove request-time containment
/// (F1): a misbehaving source must degrade to its own empty/absent result, never take
/// down a request for every other installed source.
/// </summary>
public sealed class ThrowingSource : IMediaSource
{
    public string Key => "throwing";

    // Reading this must not break /manifest.json for any other, healthy source.
    public IReadOnlyList<CatalogDescriptor> Catalogs => throw new InvalidOperationException("Catalogs boom");

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("SearchAsync boom");

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("DetailAsync boom");

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("ResolveAsync boom");

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => throw new InvalidOperationException("OpenAsync boom");
}

/// <summary>
/// Returns null where the interface's static type says it can't — a plugin author's
/// nullable annotations are not enforced at runtime, and the host must treat a null
/// SourceCatalog or a null Items list as "nothing found", not a crash.
/// </summary>
public sealed class NullishSource : IMediaSource
{
    public string Key => "nullish";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceCatalog>(null!);

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", null!));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

/// <summary>An Items list containing a null element alongside a real one — the likelier
/// plugin mistake than a null Items list altogether. Proves C1: the null element must be
/// skipped, not crash the whole projection (and the healthy element must still come back).</summary>
public sealed class NullItemSource : IMediaSource
{
    public string Key => "nullitem";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", [new CatalogItem("keep", "Keep", "", "movie"), null!]));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", [new CatalogItem("keep", "Keep", "", "movie"), null!]));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

/// <summary>An Items list whose enumerator itself throws mid-iteration — a plugin could
/// return any IReadOnlyList&lt;CatalogItem&gt; implementation, not just an array or List.
/// Proves C1: the whole projection, including enumeration, must run inside the guarded
/// region, not just the async call that produced the catalog.</summary>
file sealed class ThrowingItemsList : IReadOnlyList<CatalogItem>
{
    public CatalogItem this[int index] => throw new InvalidOperationException("indexer boom");
    public int Count => 1;
    public IEnumerator<CatalogItem> GetEnumerator() => throw new InvalidOperationException("GetEnumerator boom");
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class ThrowingEnumerationSource : IMediaSource
{
    public string Key => "throwingenum";
    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", new ThrowingItemsList()));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", new ThrowingItemsList()));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

/// <summary>
/// Key is gated by the "EBS_TEST_KEY_ARMED" process env var: unarmed (the default) it
/// returns a working key, so plugin registration, SourceRouter construction and the
/// startup warm-up loop — none of which a test controls the timing of — all see a source
/// that behaves normally and gets routed. Only once a test arms the flag around its own
/// request does Key start throwing, precisely reproducing "worked when the router was
/// built, throws now" (C2) without any fragile call-counting across the shared, ordering-
/// independent test host. SearchAsync/DetailAsync succeed with a real item, so a 500 here
/// can only come from the Key read that builds the item's wire id, not from the async call.
/// </summary>
public sealed class KeyArmableSource : IMediaSource
{
    public string Key => Environment.GetEnvironmentVariable("EBS_TEST_KEY_ARMED") == "1"
        ? throw new InvalidOperationException("Key boom (armed)")
        : "keyarmable";

    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", [new CatalogItem("x", "X", "", "movie")]));

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => Task.FromResult(new SourceCatalog("t", [new CatalogItem("x", "X", "", "movie")]));

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => Task.FromResult<SourceStream?>(null);
}

/// <summary>Same Key-arming trick as <see cref="KeyArmableSource"/>, but every request-time
/// method also throws unconditionally. Proves I1: the catch block that logs about the
/// method throwing must not ITSELF throw when Key — the very thing the log line names the
/// source by — is also broken. Before the fix, this took the request down from inside the
/// catch block that was already handling the failure.</summary>
public sealed class KeyArmableAndMethodsThrowSource : IMediaSource
{
    public string Key => Environment.GetEnvironmentVariable("EBS_TEST_KEY_ARMED") == "1"
        ? throw new InvalidOperationException("Key boom (armed)")
        : "keyarmablemethodsthrow";

    public IReadOnlyList<CatalogDescriptor> Catalogs { get; } = [];

    public Task<SourceCatalog> SearchAsync(string catalogId, string? query, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("SearchAsync boom");

    public Task<SourceCatalog> DetailAsync(string itemId, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("DetailAsync boom");

    public Task<SourceStream?> ResolveAsync(string itemId, int index, SourceContext ctx, CancellationToken ct)
        => throw new InvalidOperationException("ResolveAsync boom");

    public Task<ProxyResponse?> OpenAsync(string itemId, string? rangeHeader, CancellationToken ct)
        => throw new InvalidOperationException("OpenAsync boom");
}

public sealed class ThrowingSourcePlugin : IPlugin
{
    public string Key => "throwing";
    public string DisplayName => "Throwing Source Plugin";
    public Version ApiVersion => new(ServerApi.VersionString);

    public void Configure(IPluginRegistry registry, IPluginContext context)
    {
        registry.AddSource(new ThrowingSource());
        registry.AddSource(new NullishSource());
        registry.AddSource(new NullItemSource());
        registry.AddSource(new ThrowingEnumerationSource());
        registry.AddSource(new KeyArmableSource());
        registry.AddSource(new KeyArmableAndMethodsThrowSource());
    }
}
