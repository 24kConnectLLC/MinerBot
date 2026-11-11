using System.Diagnostics;
using System.Net;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The built program on a Unix socket, as compose.yml runs it on the server, with databases that aren't there. nginx
    // reaches it only through the socket, in a folder no other account can write to, so nothing else can take its place
    // while it restarts, as anything could with a TCP port.
    public class UnixSocketProcessTests
    {
        static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

        static CancellationToken Token => TestContext.Current.CancellationToken;

        // The proxy key in CommandModeProcessTests.GoodSettings, which nginx would send
        const string ProxyKey = "made-up-proxy-key-for-the-process-test-0123456789abcdef";

        // The dashboard, started and answering, until it's killed
        sealed class Running : IAsyncDisposable
        {
            readonly Process process;

            Running(Process process) => this.process = process;

            public static async Task<Running> StartAsync(IReadOnlyDictionary<string, string> settings, string socket)
            {
                using var client = TestSockets.Client(socket);
                return await StartAsync(settings, client);
            }

            // Started and answering through the client, which goes where the dashboard should listen
            public static async Task<Running> StartAsync(IReadOnlyDictionary<string, string> settings, HttpClient client)
            {
                var running = new Running(CommandModeProcessTests.Start(settings));
                var waited = Stopwatch.StartNew();

                while (true)
                {
                    if (running.process.HasExited)
                        Assert.Fail($"The dashboard exited with {running.process.ExitCode} before it answered: {await running.process.StandardOutput.ReadToEndAsync(Token)} {await running.process.StandardError.ReadToEndAsync(Token)}");

                    try
                    {
                        using var answer = await client.GetAsync("/health/live", Token);

                        if (answer.StatusCode == HttpStatusCode.OK)
                            return running;
                    }
                    catch (HttpRequestException)
                    {
                    }

                    if (waited.Elapsed > Limit)
                    {
                        await running.KillAsync();
                        var said = $"{await running.process.StandardOutput.ReadToEndAsync(Token)} {await running.process.StandardError.ReadToEndAsync(Token)}";
                        await running.DisposeAsync();
                        Assert.Fail($"The dashboard didn't answer where it should listen within the limit: {said}");
                    }

                    await Task.Delay(100, Token);
                }
            }

            // As the memory limit or the end of the stop's grace period would: no chance to close the socket
            public async Task KillAsync()
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);

                await process.WaitForExitAsync(Token).WaitAsync(Limit, Token);
            }

            public async ValueTask DisposeAsync()
            {
                await KillAsync();
                process.Dispose();
            }
        }

        // The good settings with the socket in the test's own folder
        static Dictionary<string, string> Settings(string keys, string socket)
        {
            var settings = CommandModeProcessTests.GoodSettings(keys);
            settings["Dashboard__UnixSocket"] = socket;
            return settings;
        }

        // As nginx sends a visitor's request: the public host, the key, the visitor's address and https
        static async Task<HttpResponseMessage> VisitAsync(HttpClient client, string visitor, bool proxyKey = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");

            if (proxyKey)
                request.Headers.Add(ProxyKeyMiddleware.HeaderName, ProxyKey);

            request.Headers.Add("X-Forwarded-For", visitor);
            request.Headers.Add("X-Forwarded-Proto", "https");

            return await client.SendAsync(request, Token);
        }

        // It listens on the socket and not on its port, the healthcheck command finds it there, and off Windows the socket is
        // open to nginx's group. Each visitor is counted by the address nginx sends, so one who goes over the limit is
        // stopped and another isn't, and without the key it's the bare 404 as before.
        [Fact]
        public async Task AnswersOnlyOnTheSocketAndCountsEachVisitor()
        {
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");
            var settings = Settings(keys, socket);

            try
            {
                await using var dashboard = await Running.StartAsync(settings, socket);

                Assert.False(await TestSockets.TcpAnswersAsync(int.Parse(settings["Dashboard__Port"]), Token), "The dashboard listens on its port too");

                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixSocketFile.Mode, File.GetUnixFileMode(socket));

                var health = await CommandModeProcessTests.RunAsync(settings, "healthcheck");
                Assert.True(health.ExitCode == 0, $"healthcheck exited {health.ExitCode}: {health.Output} {health.Errors}");
                Assert.Equal("Healthy", health.Output.Trim());

                // Plain http on the socket, as nginx sends it, to the public host name
                using var client = TestSockets.Client(socket, "http://" + DashboardFactory.PublicAddress.Host);

                // Answered before the limits, so it uses up nothing
                Assert.Equal(HttpStatusCode.NotFound, (await VisitAsync(client, "203.0.113.20", proxyKey: false)).StatusCode);

                for (var i = 0; i < RateLimits.VisitorPerMinute; i++)
                    Assert.Equal(HttpStatusCode.OK, (await VisitAsync(client, "203.0.113.20")).StatusCode);

                var limited = await VisitAsync(client, "203.0.113.20");
                Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.Equal(RateLimits.Rejection, await limited.Content.ReadAsStringAsync(Token));

                Assert.Equal(HttpStatusCode.OK, (await VisitAsync(client, "203.0.113.21")).StatusCode);
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // Killed, it leaves its socket, and the next start deletes it and listens again, as after the container's memory
        // limit or a docker kill. Fails if the start doesn't, when the container would restart over and over.
        [Fact]
        public async Task StartsAgainAfterItWasKilled()
        {
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");
            var settings = Settings(keys, socket);

            try
            {
                await using (var first = await Running.StartAsync(settings, socket))
                    await first.KillAsync();

                Assert.True(UnixSockets.IsSocket(socket), "The killed dashboard's socket isn't there");

                await using var second = await Running.StartAsync(settings, socket);
                Assert.Equal("Healthy", (await CommandModeProcessTests.RunAsync(settings, "healthcheck")).Output.Trim());
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // A hand-edited dashboard.env with ASPNETCORE_PREFERHOSTINGURLS=true and ASPNETCORE_URLS never moves the dashboard: it
        // answers on its socket, or on its port when there's none, and nothing listens at the URL. Fails if the environment
        // can turn PreferHostingUrls on, when Kestrel would listen at the URL instead and, on Linux, then stop on the missing
        // socket.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task HostingUrlsInTheEnvironmentDontMoveIt(bool withSocket)
        {
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");
            var settings = withSocket ? Settings(keys, socket) : CommandModeProcessTests.GoodSettings(keys);
            var url = DeadDatabase.ClosedPort();
            settings["ASPNETCORE_PREFERHOSTINGURLS"] = "true";
            settings["ASPNETCORE_URLS"] = $"http://127.0.0.1:{url}";

            try
            {
                using var client = withSocket
                    ? TestSockets.Client(socket)
                    : new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{settings["Dashboard__Port"]}"), Timeout = Limit, DefaultRequestHeaders = { Host = "localhost" } };

                await using var dashboard = await Running.StartAsync(settings, client);

                Assert.False(await TestSockets.TcpAnswersAsync(url, Token), "The dashboard listens at ASPNETCORE_URLS");
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // Anything at the path that isn't a socket stops the start with a plain message, and is never deleted
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FileOrFolderAtThePathStopsTheStartAndIsKept(bool folder)
        {
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");

            if (folder)
                Directory.CreateDirectory(socket);
            else
                File.WriteAllText(socket, "not a socket");

            try
            {
                var run = await CommandModeProcessTests.RunAsync(Settings(keys, socket));

                Assert.Equal(1, run.ExitCode);
                Assert.StartsWith("The dashboard didn't start, because of its settings:", run.Errors);
                Assert.Contains("- Dashboard:UnixSocket names a file or folder that isn't a socket", run.Errors);
                Assert.DoesNotContain("   at ", run.Errors);

                if (folder)
                    Assert.True(Directory.Exists(socket));
                else
                    Assert.Equal("not a socket", File.ReadAllText(socket));
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // check-settings never touches the socket: a socket a killed dashboard left is still there afterwards, for the start
        // to replace
        [Fact]
        public async Task CheckSettingsLeavesTheSocketAlone()
        {
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");
            TestSockets.LeaveSocket(socket);

            try
            {
                var run = await CommandModeProcessTests.RunAsync(Settings(keys, socket), "check-settings");

                Assert.True(run.ExitCode == 0, $"check-settings exited {run.ExitCode}: {run.Output} {run.Errors}");
                Assert.Equal("The dashboard's settings are fine.", run.Output.Trim());
                Assert.True(UnixSockets.IsSocket(socket), "check-settings deleted the socket");
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }
    }
}
