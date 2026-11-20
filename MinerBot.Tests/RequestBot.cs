using DataAccessLibrary.Requests.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MinerBot.Shared.Requests;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using SteamWebAPIAccess;

namespace MinerBot.Tests
{
    // The host's lifetime, which a restart asks to stop
    internal sealed class FakeLifetime : IHostApplicationLifetime
    {
        public int Stops;
        public Action? OnStop { get; set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Interlocked.Increment(ref Stops);
            OnStop?.Invoke();
        }
    }

    // The dashboard's requests as it signs them, with a key made up for the tests
    internal static class SignedRequests
    {
        public static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        public static readonly string KeyText = Convert.ToHexStringLower(Key);

        public const ulong OwnerId = 123456789012345678;

        public static BotRequestRow Signed(string kind, DateTime now, string role = RequestRoles.Owner, ulong? guildId = null, ulong? channelId = null, string? argument = null,
            string name = "Owner", ulong actorId = OwnerId) =>
            Row(RequestFields.New(RequestFields.NewKey(), RequestKinds.Find(kind)!, actorId, name, role, guildId, channelId, argument, now));

        public static BotRequestRow Row(RequestFields fields, byte[]? key = null) => new()
        {
            request_key = fields.RequestKey,
            kind = fields.Kind,
            actor_id = fields.ActorId,
            actor_name = fields.ActorName,
            actor_role = fields.ActorRole,
            guild_id = fields.GuildId,
            channel_id = fields.ChannelId,
            argument = fields.Argument,
            created_at = fields.CreatedAt,
            expires_at = fields.ExpiresAt,
            signature = RequestSigning.Sign(key ?? Key, fields)
        };
    }

    // The bot's request worker on FakeDiscord, with FakeRequestData for MinerBot's database, FakeSteam, and JobHandler with
    // the bot's own jobs, which run on FakeDiscord's services. A run starts at Noon and its clock is then at Now, past the
    // minutes a restart waits for. NewRun is the next run of the bot after a restart: a new run ID, start time and worker
    // over the same database.
    internal sealed class RequestBot : IAsyncDisposable
    {
        public const string Connection = "Server=127.0.0.1;Database=bot_db;Uid=minerbot-bot;Pwd=password";

        public static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public static readonly DateTimeOffset Now = Noon.AddMinutes(6);

        public FakeDiscord Discord { get; }
        public FakeRequestData Data { get; }
        public FakeSteam Steam { get; } = new() { Status = new SteamStatus(SteamConnection.LoggedOn, Noon.AddHours(-1)) };
        public FakeLifetime Lifetime { get; } = new();
        public FakeDiscordStatus? Status { get; }
        public RecordingLogger<RequestWorker> Logger { get; } = new();
        public JobHandler Jobs { get; }

        public BotRecorder Recorder { get; private set; } = null!;
        public RequestActions Actions { get; private set; } = null!;
        public RequestWorker Worker { get; private set; } = null!;

        public PostsPause Pause => Discord.Services.GetRequiredService<PostsPause>();
        public PresenceKeeper Presence => Discord.Services.GetRequiredService<PresenceKeeper>();

        RequestBot(FakeDiscord discord, FakeDiscordStatus? status, FakeRequestData? data)
        {
            Discord = discord;
            Status = status;
            Data = data ?? new FakeRequestData();
            Jobs = new JobHandler(new RecordingLogger<JobHandler>(), discord.Services);
        }

        // With MinerBot's database and the key unless a test leaves them out, and jobs found, unless a test wants them not
        // to be yet. Discord is FakeDiscord's own state, unless the test takes it over. A database given is another
        // process's, such as the bot's before a restart of its container.
        public static async Task<RequestBot> StartAsync(string? key = null, bool useKey = true, bool connection = true, bool jobs = true, bool fakeStatus = false,
            FakeRequestData? data = null)
        {
            var discord = await FakeDiscord.StartAsync();
            var bot = new RequestBot(discord, fakeStatus ? new FakeDiscordStatus { Reading = FakeDiscordStatus.Up } : null, data);

            if (connection)
                discord.Settings["ConnectionStrings:BotConnection"] = Connection;

            if (useKey)
                discord.Settings[RequestWorker.KeySetting] = key ?? SignedRequests.KeyText;

            if (jobs)
                await bot.Jobs.StartAsync(new CancellationToken(canceled: true));

            discord.Clock.UtcNow = Noon;
            bot.NewRun();
            discord.Clock.UtcNow = Now;

            return bot;
        }

        // The bot's next run: what it keeps in memory starts again, and what's in MinerBot's database stays
        public void NewRun()
        {
            Recorder = new BotRecorder(Discord.Clock, Discord.Settings);

            var alerts = new OwnerAlertService(Discord.Client, Discord.Settings, Recorder);
            var services = Discord.Services;

            Actions = ActivatorUtilities.CreateInstance<RequestActions>(services, Data, Steam, Jobs, Recorder, alerts);
            Actions.SteamPoll = TimeSpan.FromMilliseconds(10);

            var arguments = new List<object> { Data, Actions, alerts, Recorder, Jobs, Lifetime, Logger };

            if (Status != null)
                arguments.Add(Status);

            Worker = ActivatorUtilities.CreateInstance<RequestWorker>(services, arguments.ToArray());
            Worker.Interval = TimeSpan.FromMilliseconds(20);
        }

        public DateTime Clock
        {
            get => Discord.Clock.UtcNow.UtcDateTime;
            set => Discord.Clock.UtcNow = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        }

        public void Move(TimeSpan time) => Discord.Clock.UtcNow += time;

        // A request signed now, added to the table
        public ulong Add(string kind, string role = RequestRoles.Owner, ulong? guildId = null, ulong? channelId = null, string? argument = null, string name = "Owner",
            ulong actorId = SignedRequests.OwnerId) =>
            Data.Add(SignedRequests.Signed(kind, Clock, role, guildId, channelId, argument, name, actorId));

        public Task TickAsync() => Worker.TickAsync(TestContext.Current.CancellationToken);

        public BotRequestRow Row(ulong id) => Data.Row(id);

        public List<(string Kind, string? Detail)> Events(params string[] kinds) =>
            Recorder.PeekEvents(1000).Where(row => kinds.Length == 0 || kinds.Contains(row.kind)).Select(row => (row.kind, row.detail)).ToList();

        // The owner's messages: what was sent to the direct message channel
        public List<string?> Messages() => Discord.Steps.Where(step => step.Name == "send message" && step.Endpoint == $"channels/{FakeDiscord.DirectChannelId}/messages")
            .Select(step => step.Content).ToList();

        // What reached a server's channel, the owner's messages apart
        public List<Step> Posts() => Discord.Steps.Where(step => step.Name == "send message" && step.Endpoint != $"channels/{FakeDiscord.DirectChannelId}/messages").ToList();

        public ValueTask DisposeAsync() => Discord.DisposeAsync();
    }
}
