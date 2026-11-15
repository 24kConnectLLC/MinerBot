using System.Diagnostics;
using System.Net.WebSockets;
using DataAccessLibrary.Status;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using MinerBot_2._0.Status;

namespace MinerBot.Tests
{
    // When the bot counts as up on Discord, and what goes into its history about the connection
    public class DiscordStatusTests
    {
        static readonly DateTimeOffset Noon = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        readonly FakeClock clock = new() { UtcNow = Noon };

        // A client that never connects, whose state the test sets
        sealed class TestClient : DiscordSocketClient
        {
            public ConnectionState State = ConnectionState.Disconnected;

            public override ConnectionState ConnectionState => State;
            public override int Latency { get => 42; protected set { } }
        }

        static List<string> Recorded(BotRecorder recorder) =>
            recorder.PeekEvents(100).Where(row => row.kind != BotEvents.BotStarted).Select(row => $"{row.kind}: {row.detail}").ToList();

        // Discord.Net counts a new session as connected before Ready, and a resume raises no Ready at all, so up is connected
        // now after a Ready since the start. The state is the connection, Ready or not, and only a connected one counts as up
        // in the samples: an outage goes back and forth between connecting and disconnected.
        [Fact]
        public async Task UpIsConnectedAfterAReadySinceTheStart()
        {
            using var client = new TestClient();
            var handler = new DiscordStatusHandler(client, new BotRecorder(clock, BotRecorderTests.Settings()), clock);
            var connecting = new DiscordReading(DiscordState.Connecting, false, null, null);
            var disconnected = new DiscordReading(DiscordState.Disconnected, false, null, null);

            Assert.Equal(disconnected, handler.Read());
            Assert.False(handler.Read().Connected);

            client.State = ConnectionState.Connecting;
            Assert.Equal(connecting, handler.Read());
            Assert.False(handler.Read().Connected);

            client.State = ConnectionState.Connected;
            Assert.Equal(new DiscordReading(DiscordState.Connected, false, 42, 0), handler.Read());
            Assert.True(handler.Read().Connected);

            await handler.OnReady();
            Assert.Equal(new DiscordReading(DiscordState.Connected, true, 42, 0), handler.Read());

            client.State = ConnectionState.Disconnecting;
            Assert.Equal(disconnected, handler.Read());

            client.State = ConnectionState.Disconnected;
            Assert.Equal(disconnected, handler.Read());

            // A resumed session
            client.State = ConnectionState.Connected;
            Assert.True(handler.Read().Up);
        }

        // Each failed attempt to connect again raises Disconnected too, so an outage records each cause once, and is down
        // from the drop that began it. The next outage records its causes afresh. Never Discord's own text, only the
        // exception's type and the close code.
        [Fact]
        public async Task ConnectionsAndDropsAreRecordedOnceEach()
        {
            using var client = new TestClient();
            var recorder = new BotRecorder(clock, BotRecorderTests.Settings());
            var handler = new DiscordStatusHandler(client, recorder, clock);
            var closed = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "WebSocket connection was closed",
                new WebSocketClosedException(4000, "Discord's own words"));

            clock.UtcNow = Noon.AddSeconds(3);
            await handler.OnConnected();

            clock.UtcNow = Noon.AddHours(1);
            await handler.OnDisconnected(closed);
            clock.UtcNow = Noon.AddHours(1).AddSeconds(30);
            await handler.OnDisconnected(closed);
            await handler.OnDisconnected(new TimeoutException("Discord's own words"));
            clock.UtcNow = Noon.AddHours(1).AddSeconds(60);
            await handler.OnDisconnected(new TimeoutException());

            clock.UtcNow = Noon.AddHours(1).AddSeconds(90);
            await handler.OnConnected();
            await handler.OnDisconnected(new GatewayReconnectException("Discord's own words"));
            await handler.OnDisconnected(new TimeoutException());
            await handler.OnDisconnected(null);

            Assert.Equal(
            [
                "discord.connected: the first connection of this run, 3 s after the start",
                "discord.disconnected: WebSocketException, close code 4000",
                "discord.disconnected: TimeoutException",
                "discord.connected: after 90 s down, since 2026-11-14 13:00:00 UTC",
                "discord.disconnected: GatewayReconnectException",
                "discord.disconnected: TimeoutException",
                "discord.disconnected: no reason given"
            ], Recorded(recorder));
            Assert.Equal(EventLevel.Warning, recorder.PeekEvents(100).First(row => row.kind == BotEvents.DiscordDisconnected).level);
        }
    }

    // The real Discord.Net, on a gateway that answers as Discord does, with the handlers attached as Program.cs attaches them
    [Collection("Discord")]
    public class DiscordStatusGatewayTests
    {
        [Fact]
        public async Task BotIsUpOnceDiscordIsReady()
        {
            await using var discord = await FakeDiscord.StartAsync(holdReady: true);

            Assert.False(discord.DiscordStatus.Read().Up);

            await discord.ReleaseReadyAsync();
            await WaitUntilAsync(() => discord.DiscordStatus.Read().Up);

            Assert.Equal(new DiscordReading(DiscordState.Connected, true, 0, 1), discord.DiscordStatus.Read());
            Assert.Contains(discord.Recorder.PeekEvents(100), row => row.kind == BotEvents.DiscordConnected);
        }

        static async Task WaitUntilAsync(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "The bot never counted as up");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }
    }

    // The real Discord.Net when the first session's connection drops while its servers are still arriving, and the bot
    // resumes that session, as Discord lets it: Discord.Net skips that session's Ready, and a resume never raises one
    public class DiscordResumeBeforeReadyTests : IDisposable
    {
        readonly string folder = Directory.CreateTempSubdirectory("MinerBotResumeTests").FullName;

        public void Dispose() => Directory.Delete(folder, recursive: true);

        // The status row and the samples say what the connection is, while the heartbeat, which Docker's health check and a
        // deploy read, still waits for a Ready, which the bot's own Ready handlers, such as the one that registers its
        // commands, never got
        [Fact]
        public async Task ResumedSessionWithoutReadyIsConnectedButNotReady()
        {
            var gateway = new ResumingGateway();
            using var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayHost = "wss://gateway.invalid",
                GatewayIntents = GatewayIntents.Guilds,
                LogGatewayIntentWarnings = false,
                WebSocketProvider = () => gateway
            });
            var recorder = new BotRecorder(TimeProvider.System, BotRecorderTests.Settings());
            var handler = new DiscordStatusHandler(client, recorder, TimeProvider.System);
            var readies = 0;

            handler.Initialize();
            client.Ready += () =>
            {
                Interlocked.Increment(ref readies);
                return Task.CompletedTask;
            };

            await client.LoginAsync(TokenType.Bot, "token", validateToken: false);
            await client.StartAsync();

            try
            {
                await WaitUntilAsync(() => client.ConnectionState == ConnectionState.Connected, "The bot never connected");
                await gateway.CloseAsync(new WebSocketClosedException(4000, "Discord's own words"));
                await WaitUntilAsync(() => gateway.Resumed && client.ConnectionState == ConnectionState.Connected && client.Guilds.All(guild => guild.IsConnected),
                    "The bot never resumed its session");

                var reading = handler.Read();

                Assert.Equal(0, Volatile.Read(ref readies));
                Assert.Equal((DiscordState.Connected, false, (int?)1), (reading.State, reading.Up, reading.Guilds));
                Assert.NotNull(reading.LatencyMs);

                var heartbeat = Path.Combine(folder, "minerbot-heartbeat");
                var sampler = new StatusSampler(recorder, handler, new FakeSteam(), TimeProvider.System, new RecordingLogger<StatusSampler>()) { HeartbeatPath = heartbeat };
                sampler.Tick();

                Assert.True(Assert.Single(recorder.PeekSamples(10)).discord_up);
                Assert.Equal(DiscordState.Connected, recorder.StatusToWrite().Row.discord_state);
                Assert.False(File.Exists(heartbeat));
            }
            finally
            {
                await client.StopAsync();
            }
        }

        static async Task WaitUntilAsync(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), failure);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        // Identify gets READY with the server still unavailable, and its GUILD_CREATE is held back; a Resume gets the events
        // the bot missed, then RESUMED, as Discord sends them; a heartbeat gets its ack
        sealed class ResumingGateway : Discord.Net.WebSockets.IWebSocketClient
        {
            const string Hello = """{"op":10,"d":{"heartbeat_interval":41250}}""";

            readonly SemaphoreSlim one = new(1, 1);
            int sequence, resumed;

            public bool Resumed => Volatile.Read(ref resumed) == 1;

            public event Func<byte[], int, int, Task>? BinaryMessage { add { } remove { } }
            public event Func<string, Task>? TextMessage;
            public event Func<Exception, Task>? Closed;

            public void SetHeader(string key, string value) { }
            public void SetCancelToken(CancellationToken cancelToken) { }
            public Task ConnectAsync(string host) => Task.CompletedTask;
            public Task DisconnectAsync(int closeCode = 1000) => Task.CompletedTask;
            public void Dispose() { }

            public Task SendAsync(byte[] data, int index, int count, bool isText)
            {
                using var json = System.Text.Json.JsonDocument.Parse(data.AsMemory(index, count));

                switch (json.RootElement.GetProperty("op").GetInt32())
                {
                    case 1:
                        _ = Task.Run(() => RaiseAsync("""{"op":11,"d":null}"""));
                        break;
                    case 2:
                        _ = Task.Run(async () =>
                        {
                            await RaiseAsync(Hello);
                            await DispatchAsync("READY", $$"""
                                {"v":10,"user":{{User}},"session_id":"session","resume_gateway_url":"wss://gateway.invalid","private_channels":[],
                                 "application":{"id":"1000","flags":0},"guilds":[{"id":"2000","unavailable":true}]}
                                """);
                        });
                        break;
                    case 6:
                        _ = Task.Run(async () =>
                        {
                            await RaiseAsync(Hello);
                            await DispatchAsync("GUILD_CREATE", Guild);
                            await DispatchAsync("RESUMED", "{}");
                            Interlocked.Exchange(ref resumed, 1);
                        });
                        break;
                }

                return Task.CompletedTask;
            }

            public Task CloseAsync(Exception error) => Task.Run(() => Closed?.Invoke(error) ?? Task.CompletedTask);

            Task DispatchAsync(string type, string json) =>
                RaiseAsync($$"""{"op":0,"s":{{Interlocked.Increment(ref sequence)}},"t":"{{type}}","d":{{json}}}""");

            // One at a time, as the gateway's receive loop delivers them
            async Task RaiseAsync(string frame)
            {
                await one.WaitAsync();

                try
                {
                    if (TextMessage is { } received)
                        await received(frame);
                }
                finally
                {
                    one.Release();
                }
            }

            const string User = """{"id":"1000","username":"bot","discriminator":"0","global_name":null,"avatar":null,"bot":true}""";

            static readonly string Guild = $$"""
                {"id":"2000","name":"Guild","owner_id":"5000","verification_level":0,"default_message_notifications":0,"explicit_content_filter":0,
                 "mfa_level":0,"premium_tier":0,"nsfw_level":0,"features":[],"emojis":[],"stickers":[],
                 "roles":[{"id":"2000","name":"@everyone","permissions":"0","position":0,"color":0,"hoist":false,"managed":false,"mentionable":false}],
                 "channels":[{"id":"3000","type":0,"name":"general","position":0,"permission_overwrites":[]}],
                 "unavailable":false,"member_count":1,"large":false,
                 "members":[{"user":{{User}},"roles":[],"joined_at":"2026-01-01T00:00:00+00:00","deaf":false,"mute":false}],
                 "presences":[],"threads":[],"voice_states":[],"guild_scheduled_events":[],"joined_at":"2020-01-01T00:00:00+00:00"}
                """;
        }
    }
}
