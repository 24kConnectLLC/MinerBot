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
        // it. Fails if Kestrel binds any other address, such as every one, or ignores either setting.
        [Theory]
        [InlineData(null)]
        [InlineData("127.0.0.2")]
        public async Task ListensWhereTheSettingsSay(string? listenAddress)
        {
            var token = TestContext.Current.CancellationToken;
            var port = DeadDatabase.ClosedPort();
            var imagePort = DeadDatabase.ClosedPort();
            var keys = DashboardFactory.NewKeysFolder();

            try
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, imagePort.ToString(CultureInfo.InvariantCulture));

                // Only the settings AddDashboardHosting reads, made up
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dashboard:PublicOrigin"] = DashboardFactory.PublicOrigin,
                    ["Dashboard:KeysFolder"] = keys,
                    ["Dashboard:ListenAddress"] = listenAddress,
                    ["Dashboard:Port"] = port.ToString(CultureInfo.InvariantCulture)
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
    }
}
