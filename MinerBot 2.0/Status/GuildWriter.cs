using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using Discord;
using Discord.WebSocket;

namespace MinerBot_2._0.Status
{
    // Keeps MinerBot's copy of the servers the bot is in current, for the dashboard's servers pages and the server admins'
    // channel pickers. Every 5 seconds it takes each server GuildWatcher noted from Discord.Net's cache: its row, and its
    // text and announcement channels with the bot's permissions in each. It writes one only when it differs from what it
    // last wrote of that server, so a burst of events in one server costs one write and an unchanged server costs nothing,
    // and a server noted as left is written as left. At each new session it also finds the servers the table has that
    // Discord no longer lists, which the bot left while it was offline, and every 15 minutes it notes every server, in case
    // an event never came. A write that fails keeps every note and waits 30 seconds, then twice as long each time, up to 5
    // minutes, with one warning when the failures start and one line when they end. Without BotConnection it writes
    // nothing, and the bot posts as before. Nothing leaves its loop, because an exception there would stop the whole bot.
    public sealed class GuildWriter : BackgroundService
    {
        static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
        static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(5);
        static readonly TimeSpan FullPassEvery = TimeSpan.FromMinutes(15);

        // The most writes one tick makes, so a start, when every server is new to the writer, takes a few ticks, and a stop
        // doesn't wait for all of them
        internal const int WritesPerTick = 100;

        // A server that went while the bot was offline, whose time is when the bot noticed
        internal const string GoneDetail = "found gone when the bot connected";

        // Shorter in tests, which move the clock themselves
        internal TimeSpan Interval = TimeSpan.FromSeconds(5);

        readonly DiscordSocketClient _client;
        readonly GuildWatcher _watcher;
        readonly IBotGuildData _data;
        readonly BotRecorder _recorder;
        readonly TimeProvider _clock;
        readonly ILogger<GuildWriter> _logger;
        readonly FailureLog _failures;

        // Why nothing is written, or null when BotConnection can be used
        readonly string? _unusable;

        // What was last written of each server
        readonly Dictionary<ulong, (BotGuild Guild, BotGuildChannel[] Channels)> written = new();

        DateTimeOffset? failingSince, lastFullPass;
        DateTimeOffset retryAt;
        TimeSpan retryDelay;
        uint eventSeq;

        public GuildWriter(DiscordSocketClient client, GuildWatcher watcher, IBotGuildData data, BotRecorder recorder, IConfiguration configuration, TimeProvider clock,
            ILogger<GuildWriter> logger)
        {
            _client = client;
            _watcher = watcher;
            _data = data;
            _recorder = recorder;
            _clock = clock;
            _logger = logger;
            _failures = new FailureLog(logger);
            _unusable = StatusWriter.BotConnectionProblem(configuration);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // The status writer has warned about it already
            if (_unusable != null)
            {
                _logger.LogInformation("ConnectionStrings:BotConnection {Problem}, so the servers and channels the bot is in aren't recorded either", _unusable);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(Interval, _clock, stoppingToken);
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    _failures.Log(e, "The server writer failed");
                }
            }
        }

        // One tick: a new session's check, then the servers noted since, unless an earlier failure's wait isn't over.
        // Nothing while the gateway isn't connected, when Discord.Net shows every server as unavailable. What a stop cuts
        // short is noted again at the next start, whose session notes every server.
        internal async Task TickAsync(CancellationToken stop)
        {
            var now = _clock.GetUtcNow();

            if (failingSince != null && now < retryAt)
                return;

            if (_client.ConnectionState != ConnectionState.Connected)
                return;

            if (lastFullPass == null || now - lastFullPass >= FullPassEvery)
            {
                _watcher.NoteAll(_client.Guilds.Select(guild => guild.Id));
                lastFullPass = now;
            }

            try
            {
                await WriteAsync(now.UtcDateTime, stop);
            }
            catch (Exception e) when (!(e is OperationCanceledException && stop.IsCancellationRequested))
            {
                Failed(e, now);
                return;
            }

            if (failingSince != null)
            {
                failingSince = null;
                _logger.LogInformation("MinerBot's database takes the bot's servers and channels again");
            }
        }

        async Task WriteAsync(DateTime now, CancellationToken stop)
        {
            if (_watcher.PeekReady() is { } ready)
            {
                await NewSessionAsync(ready.Unavailable, now, stop);
                _watcher.ReadyWritten(ready.Version);
            }

            var writes = 0;

            foreach (var (guildId, version, leftAt) in _watcher.PeekNotes())
            {
                if (writes >= WritesPerTick)
                    return;

                if (leftAt is { } left)
                {
                    await LeaveAsync(guildId, left, null, stop);
                    _watcher.LeaveWritten(guildId, left);
                    writes++;
                }

                // Not in the cache: left, which is noted with it or comes right after, or gone with a session that ended, which
                // the next session's check finds
                if (_client.GetGuild(guildId) is not { } guild)
                {
                    _watcher.Written(guildId, version);
                    continue;
                }

                // Just joined: it's written once Discord.Net has marked it connected, which it does after every JoinedGuild
                // handler, the welcome message's included, and its note stays until then
                if (!guild.IsConnected && _watcher.Joining(guildId))
                    continue;

                (BotGuild Guild, BotGuildChannel[] Channels)? taken;

                try
                {
                    taken = Snapshot(guild);
                }
                catch (Exception e)
                {
                    // The next event, or the next 15 minutes, takes it again
                    _failures.Log(e, "A server couldn't be read from Discord.Net's cache");
                    _watcher.Written(guildId, version);
                    continue;
                }

                // Its note stays until the cache has the server's data
                if (taken is not { } snapshot)
                    continue;

                // The gateway dropped during this tick, and Discord.Net then shows every server as unavailable, so the rest
                // waits, noted. It marks the connection as going before it marks the servers, so a snapshot that gets past this
                // was taken before they were.
                if (_client.ConnectionState != ConnectionState.Connected)
                    return;

                if (written.TryGetValue(guildId, out var last) && Same(last, snapshot))
                {
                    _watcher.Written(guildId, version);
                    continue;
                }

                Task<bool> saving;

                try
                {
                    saving = _data.SaveGuild(snapshot.Guild, snapshot.Channels, now, _recorder.RunId, eventSeq + 1);
                }
                catch (ArgumentException e)
                {
                    // The repository checks every row before any SQL. A server it refuses is the bot's own mistake, and would
                    // fail every write after it, so its note goes.
                    _failures.Log(e, "MinerBot's database refused a server the bot read, so it wasn't written");
                    _watcher.Written(guildId, version);
                    continue;
                }

                await NumberedAsync(saving, stop);

                written[guildId] = snapshot;
                _watcher.Written(guildId, version);
                writes++;
            }
        }

        // At a new session, every server in the cache is noted, and each one the table says the bot is in that Discord no
        // longer lists, not even as unavailable, is written as left: Discord.Net raises no LeftGuild for a server that went
        // while the bot wasn't connected. Those Discord still listed as unavailable when the session became ready are in an
        // outage, and the cache has nothing else of them, so only their rows are marked unavailable.
        async Task NewSessionAsync(ulong[] unavailableAtReady, DateTime now, CancellationToken stop)
        {
            var cached = _client.Guilds.ToDictionary(guild => guild.Id);
            _watcher.NoteAll(cached.Keys);

            var listedIn = (await _data.ListGuildsIn().WaitAsync(stop)).ToHashSet();

            // A leave the bot saw is written with its note, at the time it came
            var seenLeaving = _watcher.PeekNotes().Where(note => note.LeftAt != null).Select(note => note.GuildId).ToHashSet();

            foreach (var guildId in listedIn.Where(guildId => !cached.ContainsKey(guildId) && !seenLeaving.Contains(guildId)).Order())
                await LeaveAsync(guildId, now, GoneDetail, stop);

            // Unless they're back since. Discord.Net counts one Discord sent with nothing but its ID as connected, so the name it
            // doesn't have is what tells.
            var unavailable = unavailableAtReady
                .Where(guildId => listedIn.Contains(guildId) && cached.TryGetValue(guildId, out var guild) && guild.Name == null)
                .ToList();

            foreach (var batch in unavailable.Chunk(BotGuildData.BatchLimit))
            {
                await _data.MarkUnavailable(batch, now).WaitAsync(stop);

                foreach (var guildId in batch)
                    written.Remove(guildId);
            }
        }

        async Task LeaveAsync(ulong guildId, DateTime at, string? detail, CancellationToken stop)
        {
            written.Remove(guildId);

            await NumberedAsync(_data.LeaveGuild(guildId, at, _recorder.RunId, eventSeq + 1, detail), stop);
        }

        // A recorded join or leave takes the run's next number. A call that failed may have recorded its event all the same,
        // as when the database committed it but its answer didn't come within the limit, and the table would keep out the
        // next event given that number, so it isn't given again.
        async Task NumberedAsync(Task<bool> recording, CancellationToken stop)
        {
            try
            {
                if (await recording.WaitAsync(stop))
                    eventSeq++;
            }
            catch
            {
                eventSeq++;
                throw;
            }
        }

        // The server's row and its text and announcement channels as Discord.Net's cache has them, or null while the cache
        // doesn't have its data: Discord sends a server in an outage with nothing but its ID, and Discord.Net raises no event
        // once the rest arrives
        internal static (BotGuild Guild, BotGuildChannel[] Channels)? Snapshot(SocketGuild guild)
        {
            var bot = guild.CurrentUser;

            if (guild.Name == null || bot == null || guild.EveryoneRole == null)
                return null;

            // Discord.Net's TextChannels holds voice, stage and thread channels too, as their classes derive from its text
            // channel. It's the server's own list, where a channel deleted during an outage is gone, unlike the cache's other one.
            var channels = guild.TextChannels
                .Where(channel => channel.ChannelType is ChannelType.Text or ChannelType.News)
                .OrderBy(channel => channel.Id)
                .Select(channel =>
                {
                    // As DiscordService.MissingPermissions works them out, from the bot's roles and the channel's overwrites
                    var permissions = bot.GetPermissions(channel);

                    return new BotGuildChannel
                    {
                        channel_id = channel.Id,
                        name = channel.Name,
                        type = channel.ChannelType == ChannelType.News ? GuildChannelType.Announcement : GuildChannelType.Text,
                        category = channel.Category?.Name,
                        category_id = channel.CategoryId,
                        category_position = channel.Category?.Position,
                        position = channel.Position,
                        can_view = permissions.ViewChannel,
                        can_send = permissions.SendMessages,
                        can_embed = permissions.EmbedLinks
                    };
                })
                .ToArray();

            var row = new BotGuild
            {
                guild_id = guild.Id,
                name = guild.Name,
                icon_hash = guild.IconId,
                member_count = guild.MemberCount >= 0 ? (uint)guild.MemberCount : null,
                bot_joined_at = bot.JoinedAt?.UtcDateTime,
                available = guild.IsConnected,
                bot_administrator = bot.GuildPermissions.Administrator,
                // Discord.Net counts every permission for an Administrator, so these are the roles' own
                bot_permissions = bot.Roles.Aggregate(0UL, (bits, role) => bits | role.Permissions.RawValue)
            };

            return (row, channels);
        }

        // The member count isn't compared, because only a server new to the table, or back in it, takes it
        static bool Same((BotGuild Guild, BotGuildChannel[] Channels) last, (BotGuild Guild, BotGuildChannel[] Channels) now) =>
            last.Guild with { member_count = null } == now.Guild with { member_count = null } && last.Channels.SequenceEqual(now.Channels);

        void Failed(Exception e, DateTimeOffset now)
        {
            if (failingSince == null)
            {
                failingSince = now;
                retryDelay = FirstRetry;

                // Only the type and MariaDB's number, because MySql.Data's message can name the login
                _logger.LogWarning("MinerBot's database didn't take the bot's servers and channels ({Problem}). The bot keeps what changed and tries again in 30 seconds, then less often, up to every 5 minutes.",
                    FailureLog.Describe(e));
            }
            else
            {
                retryDelay = retryDelay * 2 < LongestRetry ? retryDelay * 2 : LongestRetry;
            }

            retryAt = now + retryDelay;
        }
    }
}
