using System.Runtime.CompilerServices;
using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Steam.Models;
using Microsoft.Extensions.Internal;
using Steam.Models.SteamCommunity;
using SteamKit2;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;
using SteamWebAPIAccess.Murder_Miners_Networking;

namespace MinerBot.Tests
{
    // Each fake reports its calls to the step list, so tests see them in order with the Discord requests.
    // Anything the tests don't use throws, so a new call to the data layer can't pass by accident.

    internal sealed class FakeGetData(Action<string> addStep) : IGetData
    {
        public const uint MurderMinersAppId = 274900;
        public const ulong VanitySteamId = 76561197960265729;

        public List<PublishedFileDetailsModel> LatestItems { get; set; } = Enumerable.Range(1, 5).Select(CreateItem).ToList();
        public PublishedFileDetailsModel? WorkshopItem { get; set; } = CreateItem(1);
        public PlayerSummaryModel? PlayerSummary { get; set; } = new()
        {
            Nickname = "Player",
            AvatarFullUrl = "https://example.com/avatar.png",
            ProfileUrl = "https://steamcommunity.com/profiles/1"
        };

        // What Steam finds for a custom URL's name, null when nobody has it
        public ulong? VanityId { get; set; } = VanitySteamId;

        public List<LobbyInfo> Lobbies { get; set; } = new();
        public bool LobbiesAvailable { get; set; } = true;
        public Exception? LobbyFailure { get; set; }
        public Exception? Failure { get; set; }
        public ulong? RequestedId { get; private set; }
        public string? RequestedName { get; private set; }

        public uint AppID => MurderMinersAppId;

        // Steam's IDs grow over time, so a higher ID is also a newer map, and it hasn't been updated since it was made.
        // It passes Steam's checks: found, public and a Murder Miners map.
        public static PublishedFileDetailsModel CreateItem(int id) => new()
        {
            PublishedFileId = (ulong)id,
            Result = 1,
            CreatorAppId = MurderMinersAppId,
            ConsumerAppId = MurderMinersAppId,
            Visibility = PublishedFileVisibility.Public,
            Title = $"Map {id}",
            Description = "A map",
            FileUrl = new Uri("https://example.com/map"),
            Tags = new List<string>(),
            TimeCreated = DateTime.UnixEpoch.AddDays(id),
            TimeUpdated = DateTime.UnixEpoch.AddDays(id)
        };

        public Task<List<PublishedFileDetailsModel>> GetLatestWorkshopItems(int numPerPage) => Call(LatestItems.Take(numPerPage).ToList());

        public Task<PublishedFileDetailsModel?> GetWorkshopItem(ulong id)
        {
            RequestedId = id;
            return Call(WorkshopItem);
        }

        public Task<PlayerSummaryModel?> GetPlayerSummaries(ulong steamid)
        {
            RequestedId = steamid;
            return Call(PlayerSummary);
        }

        public Task<ulong?> ResolveVanityURL(string vanityURL)
        {
            RequestedName = vanityURL;
            return Call(VanityId);
        }

        public Task<LobbyListResult> GetServerList() => LobbyFailure != null
            ? Task.FromException<LobbyListResult>(LobbyFailure)
            : Call(new LobbyListResult { Available = LobbiesAvailable, Lobbies = Lobbies });

        Task<T> Call<T>(T value, [CallerMemberName] string name = "")
        {
            addStep(name);
            return Failure != null ? Task.FromException<T>(Failure) : Task.FromResult(value);
        }

        public bool IsConnected => throw new NotImplementedException();
        public void Disconnect() => throw new NotImplementedException();
        public Task<string> GetAndCreateLobby() => throw new NotImplementedException();
        public Task<string> GetInviteLink(uint lobbyID) => throw new NotImplementedException();
        public Task<string> GetLobbyData(ulong lobbyID) => throw new NotImplementedException();
        public ulong GetSteamID3To64(uint steamID3) => throw new NotImplementedException();
        public Task<List<SteamMatchmaking.Lobby.Member>> GetUsersInLobby(ulong lobbyID) => throw new NotImplementedException();
        public void Initilization() => throw new NotImplementedException();
        public string InviteUser(ulong lobbyID, ulong userSteamID) => throw new NotImplementedException();
        public Task<NetworkSessionProperties> ReadLobbyMetadata(IReadOnlyDictionary<string, string> keyPairs) => throw new NotImplementedException();
        public void Reconnect() => throw new NotImplementedException();
    }

    internal sealed class FakeArmorData(Action<string> addStep) : IArmorData
    {
        public IEnumerable<string> ArmorNames { get; set; } = ["Armor One", "Armor Two"];
        public ArmorStyle? Armor { get; set; }
        public List<ArmorStyle> AllArmor { get; set; } = [CreateArmor(1), CreateArmor(2)];
        public Exception? Failure { get; set; }
        public string? RequestedHelmet { get; private set; }

        public static ArmorStyle CreateArmor(int id) => new() { ID = id, ArmorName = $"Armor {id}" };

        public Task<IEnumerable<ArmorStyle>> GetAllArmor() => Call<IEnumerable<ArmorStyle>>(AllArmor);

        public Task<IEnumerable<string>> GetAllArmorNamesCache(TimeSpan timeSpan) => Call(ArmorNames);

        public Task<IEnumerable<string>> GetArmorNamesByHelmet(string helmetChoice)
        {
            RequestedHelmet = helmetChoice;
            return Call(ArmorNames);
        }

        public Task<ArmorStyle> GetArmorStyle(string name) => Call(Armor!);

        Task<T> Call<T>(T value, [CallerMemberName] string name = "")
        {
            addStep(name);
            return Failure != null ? Task.FromException<T>(Failure) : Task.FromResult(value);
        }

        // The colour job's writes, by armor ID
        public Dictionary<int, string> ColorArrays { get; } = new();

        public Task UpdateColorArray(int ID, string colorArray)
        {
            ColorArrays[ID] = colorArray;
            return Call(true);
        }

        public Task DeleteArmor(ArmorStyle armor) => throw new NotImplementedException();
        public Task<IEnumerable<string>> GetAllArmorNames() => throw new NotImplementedException();
        public Task InsertArmor(ArmorStyle armor) => throw new NotImplementedException();
        public Task UpdateArmor(ArmorStyle armor) => throw new NotImplementedException();
    }

    internal sealed class FakeGuildData(Action<string> addStep) : IGuildData
    {
        public Guild? Guild { get; set; }
        public List<Guild> AllGuilds { get; set; } = [CreateGuild(FakeDiscord.ChannelId)];
        public Exception? Failure { get; set; }

        // For SaveGuild only, so the reads still answer
        public Exception? SaveFailure { get; set; }

        // What SaveGuild was last given
        public Guild? Saved { get; private set; }

        // Announcements are 0, which means turned off
        public static Guild CreateGuild(ulong channelId) => new()
        {
            guild_id = FakeDiscord.GuildId,
            armor_channel_id = channelId,
            map_channel_id = channelId,
            lobby_channel_id = channelId
        };

        public Task<IEnumerable<Guild>> GetAllGuilds()
        {
            addStep(nameof(GetAllGuilds));
            return Failure != null ? Task.FromException<IEnumerable<Guild>>(Failure) : Task.FromResult<IEnumerable<Guild>>(AllGuilds);
        }

        public Task<Guild> GetGuild(string guildId)
        {
            addStep(nameof(GetGuild));
            return Task.FromResult(Guild!);
        }

        public Task SaveGuild(Guild guild)
        {
            addStep(nameof(SaveGuild));
            Saved = guild;
            return SaveFailure != null ? Task.FromException(SaveFailure) : Task.CompletedTask;
        }

        public Task DeleteGuild(ulong guildId)
        {
            addStep(nameof(DeleteGuild));
            return Task.CompletedTask;
        }
    }

    // A memory cache takes an ISystemClock and the notification job a TimeProvider, so one clock can drive both
    internal sealed class FakeClock : TimeProvider, ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    // Steam for the worker, the status sampler and the dashboard's requests: it starts its pump, gives its status, renews
    // with the outcome a test sets, and reconnects by taking the status a test sets
    internal sealed class FakeSteam : ISteamAPIAccess
    {
        public TaskCompletionSource PumpStarted { get; } = new();
        public int Renewals, Reconnects;
        public Exception? PumpFailure;

        public TokenRenewal Renewal { get; set; } = new(TokenRenewalOutcome.Kept, new DateOnly(2027, 1, 1));

        // Holds the renewal's answer until it completes, as Steam taking its time
        public Task? RenewalHold { get; set; }

        public SteamReconnect ReconnectAnswer { get; set; } = SteamReconnect.Started;

        // The status a reconnect that started leaves, as the pump logging on again
        public SteamStatus? StatusAfterReconnect { get; set; }

        public uint appID { get; set; }
        public CallbackManager manager { get; set; } = null!;
        public string PublisherWebAPIKey { get; set; } = "";
        public SteamClient steamClient { get; set; } = null!;
        public SteamMatchmaking steamMatchmaking => null!;
        public SteamUser steamUser { get; set; } = null!;
        public Func<string, Task>? AlertOwner { get; set; }
        public Action<string, string?>? RecordEvent { get; set; }
        public SteamStatus Status { get; set; } = new(SteamConnection.Offline, DateTimeOffset.UnixEpoch);

        public void AuthenticateClient() { }
        public void SetCredentials(string key, string shouldRemember, string username = "", string password = "") { }

        public Task RunCallbacksAsync(CancellationToken stoppingToken)
        {
            PumpStarted.TrySetResult();
            return PumpFailure != null ? Task.FromException(PumpFailure) : Task.Delay(Timeout.Infinite, stoppingToken);
        }

        public async Task<TokenRenewal> RenewTokenAsync()
        {
            Interlocked.Increment(ref Renewals);

            if (RenewalHold != null)
                await RenewalHold;

            return Renewal;
        }

        public SteamReconnect RequestReconnect()
        {
            Interlocked.Increment(ref Reconnects);

            if (ReconnectAnswer == SteamReconnect.Started && StatusAfterReconnect is { } status)
                Status = status;

            return ReconnectAnswer;
        }
    }

    // MinerBot's database for StatusWriter: it keeps what was written, and a test can make every call fail, wait, or be
    // refused before any SQL, as the real repository refuses a row it can't save
    internal sealed class FakeStatusData : IBotStatusData
    {
        int calls, reads;

        public readonly List<BotStatus> Statuses = new();
        public readonly List<BotEvent> Events = new();
        public readonly List<JobRun> JobRuns = new();
        public readonly List<JobStatus> Jobs = new();
        public readonly List<UptimeSample> Samples = new();
        public readonly List<string[]> KeptJobs = new();

        // The row the run before left, which the first write reads
        public BotStatus? Before { get; set; }

        public Exception? Failure { get; set; }
        public Task? Hold { get; set; }
        public bool RefuseEvents { get; set; }

        public int Calls => Volatile.Read(ref calls);
        public int Reads => Volatile.Read(ref reads);

        public Task<BotStatus?> GetStatus()
        {
            Interlocked.Increment(ref reads);
            return Answer(() => Before);
        }

        public Task SaveStatus(BotStatus status) => Answer(() => Statuses.Add(status));

        public Task AddEvents(IReadOnlyList<BotEvent> events)
        {
            if (RefuseEvents)
                throw new ArgumentException("An event's kind is words in small letters joined by one dot");

            return Answer(() => Events.AddRange(events));
        }

        public Task SaveJobRuns(IReadOnlyList<JobRun> runs) => Answer(() => JobRuns.AddRange(runs));
        public Task SaveJobStatus(IReadOnlyList<JobStatus> jobs) => Answer(() => Jobs.AddRange(jobs));
        public Task SaveSamples(IReadOnlyList<UptimeSample> samples) => Answer(() => Samples.AddRange(samples));

        public Task<int> DeleteOtherJobs(IReadOnlyCollection<string> jobs) => Answer(() =>
        {
            KeptJobs.Add(jobs.ToArray());
            return 0;
        });

        Task Answer(Action save) => Answer(() =>
        {
            save();
            return 0;
        });

        async Task<T> Answer<T>(Func<T> save)
        {
            Interlocked.Increment(ref calls);

            if (Failure is { } failure)
                throw failure;

            if (Hold is { } hold)
                await hold;

            lock (this)
                return save();
        }
    }

    // MinerBot's database for the clean-up: each rule has some rows older than its cutoff, which its chunks take, and a
    // test can make a rule fail. Every call is kept, with the cutoff and the chunk it asked for.
    internal sealed class FakeRetentionData : IRetentionData
    {
        public int AuditRows, Addresses, Events, GuildEvents, JobRuns, Requests, Days, Samples, Sessions;
        public readonly Dictionary<string, Exception> Failures = new();
        public readonly List<(string Rule, DateTime Cutoff, int Take)> Calls = new();
        public (DateTime From, DateTime To)? Totalled;

        public Task<int> DeleteAuditRows(DateTime before, int take) => Take(nameof(DeleteAuditRows), before, take, ref AuditRows);
        public Task<int> ClearAuditAddresses(DateTime before, int take) => Take(nameof(ClearAuditAddresses), before, take, ref Addresses);
        public Task<int> DeleteEvents(DateTime before, int take) => Take(nameof(DeleteEvents), before, take, ref Events);
        public Task<int> DeleteGuildEvents(DateTime before, int take) => Take(nameof(DeleteGuildEvents), before, take, ref GuildEvents);
        public Task<int> DeleteJobRuns(DateTime before, int take) => Take(nameof(DeleteJobRuns), before, take, ref JobRuns);
        public Task<int> DeleteRequests(DateTime before, int take) => Take(nameof(DeleteRequests), before, take, ref Requests);
        public Task<int> DeleteSamples(DateTime before, int take) => Take(nameof(DeleteSamples), before, take, ref Samples);
        public Task<int> DeleteSessions(DateTime before, int take) => Take(nameof(DeleteSessions), before, take, ref Sessions);

        public Task<int> TotalUptimeDays(DateTime from, DateTime to)
        {
            Calls.Add((nameof(TotalUptimeDays), from, 0));
            Totalled = (from, to);

            return Failures.TryGetValue(nameof(TotalUptimeDays), out var failure) ? Task.FromException<int>(failure) : Task.FromResult(Days);
        }

        Task<int> Take(string rule, DateTime before, int take, ref int left)
        {
            Calls.Add((rule, before, take));

            if (Failures.TryGetValue(rule, out var failure))
                return Task.FromException<int>(failure);

            var taken = Math.Min(left, take);
            left -= taken;

            return Task.FromResult(taken);
        }
    }

    // MinerBot's copy of the servers, for the server writer and the daily member count. It keeps each server's row as the
    // table would, with when the bot left it, and records a join or a leave by the table's rules, so tests see what the
    // writer wrote. A test can make every call fail or wait, a write land and then fail, or a server be refused before any
    // SQL, as the real repository refuses one it can't save.
    internal sealed class FakeBotGuildData : IBotGuildData
    {
        int calls;
        Exception? failureAfterLanding;

        // Each server's row: null while the bot is in it, otherwise when it left
        public readonly Dictionary<ulong, DateTime?> Rows = new();
        public readonly List<(BotGuild Guild, BotGuildChannel[] Channels, DateTime At)> Saved = new();
        public readonly List<(ulong GuildId, DateTime At, string? Detail)> Leaves = new();
        public readonly List<(uint Seq, ulong GuildId, string Kind)> Events = new();
        public readonly List<ulong[]> MarkedUnavailable = new();
        public readonly List<(ulong GuildId, uint Count, DateTime Day)> Counts = new();

        // The run and number of each event added
        readonly HashSet<(ulong RunId, uint Seq)> numbers = new();

        public Exception? Failure { get; set; }
        public Task? Hold { get; set; }
        public bool RefuseGuilds { get; set; }

        // Holds the servers' writes only, as a database that answers reads but is slow with a write
        public Task? SaveHold { get; set; }

        // Thrown once, after the next server's write or leave landed, as when the database committed it but its answer
        // didn't come within the call's limit
        public Exception? FailureAfterLanding { get => Volatile.Read(ref failureAfterLanding); set => Volatile.Write(ref failureAfterLanding, value); }

        public int Calls => Volatile.Read(ref calls);

        public Task<IEnumerable<ulong>> ListGuildsIn() =>
            Answer(() => (IEnumerable<ulong>)Rows.Where(row => row.Value == null).Select(row => row.Key).ToList());

        public Task<bool> SaveGuild(BotGuild guild, IReadOnlyList<BotGuildChannel> channels, DateTime at, ulong runId, uint seq)
        {
            if (RefuseGuilds)
                throw new ArgumentException("A server's row needs its name");

            return Landed(Answer(() =>
            {
                Saved.Add((guild, channels.ToArray(), at));

                var joining = !Rows.TryGetValue(guild.guild_id, out var left) || left != null;
                Rows[guild.guild_id] = null;

                if (joining)
                    AddEvent(runId, seq, guild.guild_id, GuildEventKind.Joined);

                return joining;
            }, SaveHold));
        }

        public Task<bool> LeaveGuild(ulong guildId, DateTime at, ulong runId, uint seq, string? detail) => Landed(Answer(() =>
        {
            Leaves.Add((guildId, at, detail));

            var leaving = Rows.TryGetValue(guildId, out var left) && left == null;

            if (leaving)
            {
                Rows[guildId] = at;
                AddEvent(runId, seq, guildId, GuildEventKind.Left);
            }

            return leaving;
        }));

        // As the table's unique key on the run and the number, with ON DUPLICATE KEY UPDATE id = id: an event with a number
        // the run already used isn't added, and the call that brought it doesn't know
        void AddEvent(ulong runId, uint seq, ulong guildId, string kind)
        {
            if (numbers.Add((runId, seq)))
                Events.Add((seq, guildId, kind));
        }

        async Task<bool> Landed(Task<bool> write)
        {
            var recorded = await write;

            if (Interlocked.Exchange(ref failureAfterLanding, null) is { } failure)
                throw failure;

            return recorded;
        }

        public Task<int> MarkUnavailable(IReadOnlyCollection<ulong> guildIds, DateTime at) => Answer(() =>
        {
            MarkedUnavailable.Add(guildIds.ToArray());
            return guildIds.Count;
        });

        public Task SaveMemberCount(ulong guildId, uint memberCount, DateTime day, DateTime at) => Answer(() =>
        {
            Counts.Add((guildId, memberCount, day));
            return true;
        });

        async Task<T> Answer<T>(Func<T> save, Task? saveHold = null)
        {
            Interlocked.Increment(ref calls);

            if (Failure is { } failure)
                throw failure;

            if (Hold is { } hold)
                await hold;

            if (saveHold != null)
                await saveHold;

            lock (this)
                return save();
        }
    }
}
