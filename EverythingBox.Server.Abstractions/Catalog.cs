namespace EverythingBox.Server.Abstractions;

/// <summary>A catalog a source offers. <paramref name="MediaType"/> is a protocol
/// string ("movie", "series", "comic", "manga", "book", "audiobook", "game").</summary>
public sealed record CatalogDescriptor(string Id, string Name, string MediaType);

/// <summary>Presentation for a media type the client does not know natively.
/// "movie" and "series" are built in and never need declaring.</summary>
public sealed record MediaTypeDescriptor(
    string Type,
    string Color,
    string Icon,
    string OpenKind,
    string DetailLayout);

/// <summary><paramref name="Id"/> is the source's OWN id — the host prefixes it
/// with the source key before it reaches the client.</summary>
public sealed record CatalogItem(
    string Id,
    string Title,
    string Subtitle,
    string MediaType,
    string? ThumbnailUrl = null,
    bool Expandable = false);

public sealed record SourceCatalog(string Title, IReadOnlyList<CatalogItem> Items, bool HasMore = false)
{
    public static SourceCatalog Empty(string title) => new(title, []);
}

/// <summary>A playable result. <paramref name="Curl"/> asks the client to fetch the
/// URL itself. A stream with a <paramref name="Notice"/> and no URL tells the user
/// something is in progress and to retry.</summary>
public sealed record SourceStream(string Url, string Mime, string? Notice = null, bool Curl = false)
{
    public static SourceStream FromNotice(string notice) => new("", "", notice);
}

/// <summary>What the current request knows. Grows in later plans; always
/// constructed with initialisers so added properties stay source-compatible.</summary>
public sealed record SourceContext
{
    /// <summary>The client can fetch a URL itself via curl (desktop), so a host that
    /// rejects the client's TLS fingerprint can be handed over directly instead of proxied.</summary>
    public bool ClientCanCurl { get; init; }
}

/// <summary>A byte stream the host relays on a source's behalf, for hosts the client
/// cannot fetch itself. Disposing releases both the stream and whatever owns it.</summary>
public sealed class ProxyResponse(Stream body, string contentType) : IAsyncDisposable
{
    public Stream Body { get; } = body;
    public string ContentType { get; } = contentType;
    public int StatusCode { get; init; } = 200;
    public long? ContentLength { get; init; }
    public string? AcceptRanges { get; init; }
    public string? ContentRange { get; init; }

    /// <summary>Optional owner disposed after the body — e.g. the HttpResponseMessage.</summary>
    public IDisposable? Owner { get; init; }

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        Owner?.Dispose();
    }
}

public enum WarmUpStatus { NotApplicable, Ready, Failed }

public sealed record WarmUpResult(WarmUpStatus Status, string? Detail = null)
{
    public static readonly WarmUpResult NotApplicable = new(WarmUpStatus.NotApplicable);
    public static readonly WarmUpResult Ready = new(WarmUpStatus.Ready);
    public static WarmUpResult Failed(string detail) => new(WarmUpStatus.Failed, detail);
}
