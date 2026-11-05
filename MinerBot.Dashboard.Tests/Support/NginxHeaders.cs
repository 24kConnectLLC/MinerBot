using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Tests.Support
{
    // What nginx adds to every request it passes on: the proxy key, the visitor's address and https. A header the test
    // already set is left as it is.
    internal sealed class NginxHeaders(string? proxyKey, string? forwardedFor, string? remoteAddress) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (proxyKey != null && !request.Headers.Contains(ProxyKeyMiddleware.HeaderName))
                request.Headers.Add(ProxyKeyMiddleware.HeaderName, proxyKey);

            if (forwardedFor != null && !request.Headers.Contains("X-Forwarded-For"))
                request.Headers.Add("X-Forwarded-For", forwardedFor);

            if (!request.Headers.Contains("X-Forwarded-Proto"))
                request.Headers.Add("X-Forwarded-Proto", "https");

            if (remoteAddress != null && !request.Headers.Contains(ClientAddressFilter.Header))
                request.Headers.Add(ClientAddressFilter.Header, remoteAddress);

            return base.SendAsync(request, cancellationToken);
        }
    }
}
