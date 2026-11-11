using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // TestServer ignores Kestrel, so this starts a real one with the dashboard's Kestrel settings on free loopback ports
    public class KestrelTests
    {
        static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

        // HTTP_PORTS stands in for the aspnet image's 8080. Fails if Listen is dropped, which would bind the image's port on
        // every address, if PreferHostingUrls is set, if the Server header comes back, or if the body limit is lost.
        [Fact]
        public async Task ListensOnlyWhereConfiguredWithNoServerHeaderAndABodyLimit()
        {
            var token = TestContext.Current.CancellationToken;
            var port = DeadDatabase.ClosedPort();
            var imagePort = DeadDatabase.ClosedPort();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, imagePort.ToString(CultureInfo.InvariantCulture));
            builder.Services.Configure<KestrelServerOptions>(kestrel => DashboardHosting.ConfigureKestrel(kestrel, new IPEndPoint(IPAddress.Loopback, port)));
            builder.Logging.ClearProviders();

            await using var app = builder.Build();
            app.MapGet("/", () => "answered");
            app.MapPost("/", async (HttpRequest request) =>
            {
                await request.Body.CopyToAsync(Stream.Null, request.HttpContext.RequestAborted);
                return "read";
            });

            await app.StartAsync(token).WaitAsync(Limit, token);

            try
            {
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                Assert.Equal([$"http://127.0.0.1:{port}"], addresses);

                using (var socket = new TcpClient())
                    await Assert.ThrowsAnyAsync<SocketException>(() => socket.ConnectAsync(IPAddress.Loopback, imagePort, token).AsTask().WaitAsync(Limit, token));

                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = Limit };

                var get = await client.GetAsync("/", token);
                Assert.Equal(HttpStatusCode.OK, get.StatusCode);
                Assert.False(get.Headers.Contains("Server"));

                var fits = await client.PostAsync("/", new ByteArrayContent(new byte[DashboardHosting.MaxRequestBodySize]), token);
                Assert.Equal(HttpStatusCode.OK, fits.StatusCode);

                var tooBig = await client.PostAsync("/", new ByteArrayContent(new byte[DashboardHosting.MaxRequestBodySize + 1]), token);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
            }
            finally
            {
                await app.StopAsync(CancellationToken.None).WaitAsync(Limit, CancellationToken.None);
            }
        }

        // The address comes from Dashboard:ListenAddress and Dashboard:Port, through AddDashboardHosting as Program.cs calls
        // it. Fails if Kestrel binds any other address, such as every one, ignores either setting, or also binds an endpoint
        // from the configuration's Kestrel section, which ASP.NET Core would otherwise add beside the dashboard's own.
        [Theory]
        [InlineData(null)]
        [InlineData("127.0.0.2")]
        public async Task ListensWhereTheSettingsSay(string? listenAddress)
        {
            var token = TestContext.Current.CancellationToken;
            var port = DeadDatabase.ClosedPort();
            var imagePort = DeadDatabase.ClosedPort();
            var configuredPort = DeadDatabase.ClosedPort();
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, imagePort.ToString(CultureInfo.InvariantCulture));

                // Only the settings AddDashboardHosting reads, made up, and an endpoint such as a guide might suggest
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dashboard:PublicOrigin"] = DashboardFactory.PublicOrigin,
                    ["Dashboard:KeysFolder"] = keys,
                    ["Dashboard:ListenAddress"] = listenAddress,
                    ["Dashboard:Port"] = port.ToString(CultureInfo.InvariantCulture),
                    ["Kestrel:Endpoints:Extra:Url"] = $"http://127.0.0.1:{configuredPort}"
                });

                builder.Services.AddOptions<DashboardOptions>().BindConfiguration(DashboardOptions.Section);
                builder.Services.AddDashboardHosting();
                builder.Logging.ClearProviders();

                await using var app = builder.Build();
                app.MapGet("/", () => "answered");

                await app.StartAsync(token).WaitAsync(Limit, token);

                try
                {
                    var address = listenAddress ?? DashboardOptions.DefaultListenAddress;
                    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                    Assert.Equal([$"http://{address}:{port}"], addresses);

                    using (var socket = new TcpClient())
                        await Assert.ThrowsAnyAsync<SocketException>(() => socket.ConnectAsync(IPAddress.Loopback, imagePort, token).AsTask().WaitAsync(Limit, token));

                    using (var socket = new TcpClient())
                        await Assert.ThrowsAnyAsync<SocketException>(() => socket.ConnectAsync(IPAddress.Loopback, configuredPort, token).AsTask().WaitAsync(Limit, token));

                    // It answers there, to a Host it allows
                    using var client = new HttpClient { Timeout = Limit };
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{address}:{port}/");
                    request.Headers.Host = "localhost";

                    var response = await client.SendAsync(request, token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("answered", await response.Content.ReadAsStringAsync(token));
                }
                finally
                {
                    await app.StopAsync(CancellationToken.None).WaitAsync(Limit, CancellationToken.None);
                }
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // The dashboard's hosting and nothing else, on made-up settings with the socket in the test's folder, the image's
        // port and an endpoint such as a guide might suggest. It says who connected and how, after the forwarded headers.
        static WebApplication HostingOnly(string keys, string socket, int port, int imagePort, int configuredPort, RecordingLoggerProvider? logs = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, imagePort.ToString(CultureInfo.InvariantCulture));

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:PublicOrigin"] = DashboardFactory.PublicOrigin,
                ["Dashboard:KeysFolder"] = keys,
                ["Dashboard:Port"] = port.ToString(CultureInfo.InvariantCulture),
                ["Dashboard:UnixSocket"] = socket,
                ["Kestrel:Endpoints:Extra:Url"] = $"http://127.0.0.1:{configuredPort}"
            });

            builder.Services.AddOptions<DashboardOptions>().BindConfiguration(DashboardOptions.Section);
            builder.Services.AddDashboardHosting();
            builder.Logging.ClearProviders();

            if (logs != null)
                builder.Logging.AddProvider(logs);

            var app = builder.Build();
            app.UseForwardedHeaders();
            app.MapGet("/", (HttpContext context) => $"{context.Connection.RemoteIpAddress} {context.Request.Scheme}");
            return app;
        }

        static async Task<string> AskAsync(HttpClient client, string? forwardedFor = null, string? proto = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");

            if (forwardedFor != null)
                request.Headers.Add("X-Forwarded-For", forwardedFor);

            if (proto != null)
                request.Headers.Add("X-Forwarded-Proto", proto);

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }

        // With Dashboard:UnixSocket set, as compose.yml sets it on the server, Kestrel listens on the socket and on nothing
        // else: not the port, the image's port or the configuration's endpoint. A connection there counts as loopback, as
        // nginx's did on TCP, so the forwarded headers are trusted for one hop exactly as before. Off Windows the socket is
        // opened to nginx's group once Kestrel listens. A clean stop deletes it.
        [Fact]
        public async Task ListensOnlyOnTheUnixSocketWhenOneIsSet()
        {
            var token = TestContext.Current.CancellationToken;
            var port = DeadDatabase.ClosedPort();
            var imagePort = DeadDatabase.ClosedPort();
            var configuredPort = DeadDatabase.ClosedPort();
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");

            try
            {
                await using var app = HostingOnly(keys, socket, port, imagePort, configuredPort);
                await app.StartAsync(token).WaitAsync(Limit, token);

                try
                {
                    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                    Assert.Equal([$"http://unix:{socket}"], addresses);

                    foreach (var other in new[] { port, imagePort, configuredPort })
                        Assert.False(await TestSockets.TcpAnswersAsync(other, token), $"Something listens on port {other}");

                    if (!OperatingSystem.IsWindows())
                        Assert.Equal(UnixSocketFile.Mode, File.GetUnixFileMode(socket));

                    using var client = TestSockets.Client(socket);

                    Assert.Equal("127.0.0.1 http", await AskAsync(client));
                    Assert.Equal("203.0.113.10 https", await AskAsync(client, "203.0.113.10", "https"));
                    Assert.Equal("203.0.113.10 https", await AskAsync(client, "198.51.100.1, 203.0.113.10", "https"));
                }
                finally
                {
                    await app.StopAsync(CancellationToken.None).WaitAsync(Limit, CancellationToken.None);
                }

                Assert.False(Path.Exists(socket), "A clean stop left the socket");
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // A dashboard that's killed leaves its socket, and Kestrel can't bind over it, so the next start deletes it first.
        // Fails if that's left out, when the container would restart over and over until someone deleted it.
        [Fact]
        public async Task SocketLeftBehindIsReplaced()
        {
            var token = TestContext.Current.CancellationToken;
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");
            var logs = new RecordingLoggerProvider();
            TestSockets.LeaveSocket(socket);

            try
            {
                await using var app = HostingOnly(keys, socket, DeadDatabase.ClosedPort(), DeadDatabase.ClosedPort(), DeadDatabase.ClosedPort(), logs);
                await app.StartAsync(token).WaitAsync(Limit, token);

                try
                {
                    using var client = TestSockets.Client(socket);
                    Assert.Equal("127.0.0.1 http", await AskAsync(client));
                    Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message == $"Deleted the socket at {socket}, which a dashboard that was stopped without closing it left behind");
                }
                finally
                {
                    await app.StopAsync(CancellationToken.None).WaitAsync(Limit, CancellationToken.None);
                }
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // Only a socket is ever deleted. A file or a folder at the path is left as it is, and the start fails instead.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FileOrFolderAtThePathIsNeverDeleted(bool folder)
        {
            var token = TestContext.Current.CancellationToken;
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");

            if (folder)
                Directory.CreateDirectory(Path.Combine(socket, "inside"));
            else
                File.WriteAllText(socket, "not a socket");

            try
            {
                await using var app = HostingOnly(keys, socket, DeadDatabase.ClosedPort(), DeadDatabase.ClosedPort(), DeadDatabase.ClosedPort());

                // Linux says the address is in use. Windows refuses a folder's path as an invalid argument.
                var failure = await Record.ExceptionAsync(() => app.StartAsync(token).WaitAsync(Limit, token));
                Assert.True(failure is IOException or SocketException, $"The start ended with {failure?.GetType().Name ?? "no exception"}");

                if (folder)
                    Assert.True(Directory.Exists(Path.Combine(socket, "inside")));
                else
                    Assert.Equal("not a socket", File.ReadAllText(socket));
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }

        // A socket something still listens on, such as a dashboard that's running, is never taken from it. The start fails
        // instead, and the other keeps its socket.
        [Fact]
        public async Task SocketSomethingListensOnIsLeftAlone()
        {
            var token = TestContext.Current.CancellationToken;
            var keys = DashboardFactory.NewKeysFolder();
            using var sockets = new TestSockets();
            var socket = sockets.PathOf("dashboard.sock");
            var logs = new RecordingLoggerProvider();
            using var other = TestSockets.Listening(socket);

            try
            {
                await using var app = HostingOnly(keys, socket, DeadDatabase.ClosedPort(), DeadDatabase.ClosedPort(), DeadDatabase.ClosedPort(), logs);

                await Assert.ThrowsAnyAsync<IOException>(() => app.StartAsync(token).WaitAsync(Limit, token));

                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), token).AsTask().WaitAsync(Limit, token);
                Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message == $"Something still listens on the socket at {socket}, so it's left as it is, and this dashboard can't listen there");
            }
            finally
            {
                DashboardFactory.DeleteFolder(keys);
            }
        }
    }
}
