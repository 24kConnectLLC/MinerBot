using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace MinerBot.Dashboard.Tests.Support
{
    // TestServer leaves the remote address empty, and the forwarded headers trust an empty one, which would make every
    // X-Forwarded-For count and the forwarded-header tests pass for the wrong reason. So before anything else runs, the
    // remote address is 127.0.0.1, where nginx is, or whatever a test names in its own header.
    internal sealed class ClientAddressFilter : IStartupFilter
    {
        public const string Header = "X-Test-Remote-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var named = context.Request.Headers[Header];
                context.Request.Headers.Remove(Header);
                context.Connection.RemoteIpAddress = named.Count == 1 ? IPAddress.Parse(named[0]!) : IPAddress.Loopback;

                return nextMiddleware(context);
            });

            next(app);
        };
    }
}
