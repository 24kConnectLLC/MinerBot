namespace MinerBot.Dashboard.Tests.Support
{
    // IHttpClientFactory disposes the handlers it rotates, and the fake Discord belongs to the factory for the whole test,
    // so the client gets it through this, which leaves it alone
    internal sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
