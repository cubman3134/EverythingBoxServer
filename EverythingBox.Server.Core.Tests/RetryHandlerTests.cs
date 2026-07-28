using System.Net;
using System.Net.Http.Headers;
using EverythingBox.Server.Core.Http;
using Xunit;

namespace EverythingBox.Server.Core.Tests;

public class RetryHandlerTests
{
    private static HttpClient Client(SequenceHandler inner, int maxRetries = 3)
        => new(new RetryHandler(maxRetries, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5), innerHandler: inner));

    [Fact]
    public async Task RetriesOn429ThenSucceeds()
    {
        var inner = new SequenceHandler(call => call < 2
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Client(inner).GetAsync("http://test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls); // two 429s, then success
    }

    [Fact]
    public async Task RetriesOnServiceUnavailable()
    {
        var inner = new SequenceHandler(call => call < 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK));

        var response = await Client(inner).GetAsync("http://test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetriesAndReturnsLastResponse()
    {
        var inner = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var response = await Client(inner, maxRetries: 2).GetAsync("http://test/");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, inner.Calls); // initial + 2 retries
    }

    [Fact]
    public async Task DoesNotRetryNonTransientStatus()
    {
        var inner = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var response = await Client(inner).GetAsync("http://test/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task HonorsRetryAfterHeader()
    {
        var inner = new SequenceHandler(call =>
        {
            if (call >= 1)
                return new HttpResponseMessage(HttpStatusCode.OK);
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });

        var response = await Client(inner).GetAsync("http://test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task RetriesOnNetworkErrorThenSucceeds()
    {
        var calls = 0;
        var inner = new SequenceHandler(_ =>
        {
            if (calls++ < 1)
                throw new HttpRequestException("connection reset");
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var response = await Client(inner).GetAsync("http://test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> factory) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = factory(Calls);
            Calls++;
            return Task.FromResult(response);
        }
    }
}
