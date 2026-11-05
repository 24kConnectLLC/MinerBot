using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Settings;

namespace MinerBot.Dashboard.Tests
{
    // The gate on its own, without a server
    public class ProxyKeyMiddlewareTests
    {
        // Made up
        const string Key = "4f3c2a1b0e9d8c7b6a5f4e3d2c1b0a994f3c2a1b0e9d8c7b6a5f4e3d2c1b0a99";

        sealed class Next
        {
            public bool Ran { get; private set; }
            public bool HeaderWasThere { get; private set; }

            public Task Invoke(HttpContext context)
            {
                Ran = true;
                HeaderWasThere = context.Request.Headers.ContainsKey(ProxyKeyMiddleware.HeaderName);
                return Task.CompletedTask;
            }
        }

        static async Task<(DefaultHttpContext Context, Next Next)> RunAsync(string host, string path, params string[] keys)
        {
            var next = new Next();
            var gate = new ProxyKeyMiddleware(next.Invoke, Options.Create(new DashboardOptions { ProxyKey = Key }), NullLogger<ProxyKeyMiddleware>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Host = new HostString(host);
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();

            if (keys.Length > 0)
                context.Request.Headers[ProxyKeyMiddleware.HeaderName] = keys;

            await gate.InvokeAsync(context);
            return (context, next);
        }

        [Fact]
        public async Task RightKeyPassesAndIsGoneBeforeTheRestRuns()
        {
            var (context, next) = await RunAsync("dashboard.test", "/admin", Key);

            Assert.True(next.Ran);
            Assert.False(next.HeaderWasThere);
            Assert.False(context.Request.Headers.ContainsKey(ProxyKeyMiddleware.HeaderName));
        }

        // The same length as the real one, a different length, and empty
        public static TheoryData<string> WrongKeys => new() { new string('f', Key.Length), "short", "" };

        [Theory]
        [MemberData(nameof(WrongKeys))]
        public async Task WrongKeyGetsABare404(string key)
        {
            var (context, next) = await RunAsync("dashboard.test", "/admin", key);

            Assert.False(next.Ran);
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
            Assert.Equal(0, context.Response.Body.Length);
            Assert.False(context.Request.Headers.ContainsKey(ProxyKeyMiddleware.HeaderName));
        }

        [Fact]
        public async Task TwoValuesGetA404EvenWhenOneIsRight()
        {
            var (context, next) = await RunAsync("dashboard.test", "/admin", Key, Key);

            Assert.False(next.Ran);
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        }

        [Fact]
        public async Task NoKeyGetsA404()
        {
            var (context, next) = await RunAsync("dashboard.test", "/", []);

            Assert.False(next.Ran);
            Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        }

        // The container's own health check comes from inside it, without nginx. Only the health paths, only on localhost.
        [Theory]
        [InlineData("localhost", "/health/live", true)]
        [InlineData("localhost:5080", "/health/ready", true)]
        [InlineData("LOCALHOST", "/Health/Live", true)]
        [InlineData("localhost", "/", false)]
        [InlineData("localhost", "/admin", false)]
        [InlineData("dashboard.test", "/health/live", false)]
        [InlineData("127.0.0.1", "/health/live", false)]
        public async Task OnlyALocalHealthCheckNeedsNoKey(string host, string path, bool passes)
        {
            var (context, next) = await RunAsync(host, path, []);

            Assert.Equal(passes, next.Ran);

            if (!passes)
                Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        }
    }
}
