using System.Net;

namespace MarketData.Sources.Tests;

/// <summary>Captures the request URI and returns a canned body. No network.</summary>
public sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    : HttpMessageHandler
{
    public Uri? LastRequestUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body)
        });
    }
}

/// <summary>Returns a different canned body per URL path segment.</summary>
public sealed class RoutingStubHandler(IReadOnlyDictionary<string, string> bodyByPath)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        var body = bodyByPath.TryGetValue(path, out var b) ? b : "{}";

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });
    }
}
