using System.Net;

namespace EverythingBox.Server.Core.Http;

/// <summary>
/// An <see cref="HttpClient"/> handler that transparently retries transient
/// failures — HTTP 429 (rate limited) and 502/503/504, plus network errors —
/// with exponential backoff and jitter. Honors a <c>Retry-After</c> response
/// header when present. Wrap an <see cref="HttpClient"/> with this so every
/// provider, debrid service, and download client gets resilience for free, with
/// no external dependency.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;

    public RetryHandler(
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        HttpMessageHandler? innerHandler = null)
    {
        InnerHandler = innerHandler ?? new HttpClientHandler();
        _maxRetries = Math.Max(0, maxRetries);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(20);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (attempt >= _maxRetries || !IsTransient(response.StatusCode))
                    return response;

                var delay = RetryAfter(response) ?? Backoff(attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                await Task.Delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode code) => code is
        HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        var delay = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is not { } value)
            return null;

        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;
        return value > _maxDelay ? _maxDelay : value;
    }

    private TimeSpan Backoff(int attempt)
    {
        // exponential (base * 2^attempt), capped, with up to +25% jitter
        var exponential = _baseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, _maxDelay.TotalMilliseconds);
        var jittered = capped + (Random.Shared.NextDouble() * 0.25 * capped);
        return TimeSpan.FromMilliseconds(Math.Min(jittered, _maxDelay.TotalMilliseconds));
    }
}
