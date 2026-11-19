using System.Globalization;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Guilds;
using DataAccessLibrary.Guilds.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using DataAccessLibrary.Tests.Database;

namespace MinerBot.Dashboard.Tests.Support
{
    // The bot's status, history and servers as the bot writes them, through its own repositories on the fixture, so the
    // pages read what the bot really writes. Times are given, from the factory's clock.
    internal static class StatusSteps
    {
        public const ulong RunId = 18446744073709551615;
        public const string Commit = "5a62c9fcc6112c68e671285da53fb25369ea1407";

        // A server the bot is in, with settings, and one the website has settings for that the bot isn't in
        public const string ServerId = "1000000000000000701";
        public const string SettingsOnlyId = "1000000000000000702";

        // View Channel, Send Messages and Embed Links
        public const ulong Permissions = 1024 | 2048 | 16384;

        public static ulong Id(string text) => ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

        // A bot that's up on Discord and Steam, which last checked in at the given time
        public static BotStatus Status(DateTime heartbeat) => new()
        {
            run_id = RunId,
            version = Commit,
            started_at = heartbeat.AddHours(-2),
            heartbeat_at = heartbeat,
            discord_state = DiscordState.Connected,
            discord_since = heartbeat.AddHours(-2).AddSeconds(1),
            discord_latency_ms = 18,
            discord_guilds = 10,
            steam_state = SteamState.LoggedOn,
            steam_since = heartbeat.AddHours(-2).AddSeconds(1),
            steam_token_expires = DateOnly.FromDateTime(heartbeat).AddDays(196)
        };

        public static BotEvent Event(uint seq, DateTime at, string kind, string level, string? detail = null) => new()
        {
            run_id = RunId,
            seq = seq,
            at = at,
            kind = kind,
            level = level,
            detail = detail
        };

        public static JobStatus Job(string job, string schedule, DateTime now, string? result = null, string? detail = null) => new()
        {
            job = job,
            schedule = schedule,
            next_run_at = now.AddMinutes(4),
            last_started_at = result == null ? null : now.AddMinutes(-1),
            last_ended_at = result == null ? null : now.AddMinutes(-1).AddSeconds(2),
            last_result = result,
            last_duration_ms = result == null ? null : 1234,
            last_detail = detail,
            updated_at = now.AddMinutes(-1)
        };

        public static JobRun Run(uint seq, string job, DateTime started, string result, string? detail = null) => new()
        {
            run_id = RunId,
            seq = seq,
            job = job,
            started_at = started,
            ended_at = started.AddSeconds(2),
            result = result,
            duration_ms = 2000,
            detail = detail
        };

        // Everything the status box and the Bot status page draw: the row, a start, a connection, a routine reconnect and
        // another drop, the five jobs, each but the clean-up with a run, and the last ten minutes sampled
        public static async Task StatusAsync(MariaDbFixture db, DateTime now)
        {
            var status = db.Status();

            await status.SaveStatus(Status(now.AddSeconds(-10)));
            await status.AddEvents(
            [
                Event(1, now.AddHours(-2), "bot.started", EventLevel.Info, "version " + Commit),
                Event(2, now.AddHours(-2).AddSeconds(1), "discord.connected", EventLevel.Info, "the first connection of this run, 1 s after the start"),
                Event(3, now.AddHours(-1), RoutineReconnect.Kind, EventLevel.Warning, RoutineReconnect.Detail),
                Event(4, now.AddHours(-1).AddSeconds(1), "discord.connected", EventLevel.Info, "after 1 s down, since 2026-11-18 00:34:42 UTC"),
                Event(5, now.AddMinutes(-30), RoutineReconnect.Kind, EventLevel.Warning, "WebSocketException, close code 1001"),
                Event(6, now.AddMinutes(-30).AddSeconds(2), "discord.connected", EventLevel.Info, "after 2 s down, since 2026-11-18 00:34:42 UTC")
            ]);
            await status.SaveJobStatus(
            [
                Job("ArmorJob.AddColorArrays", "0 * * * *", now, JobResult.Ok),
                Job("GuildMembersJob.CountMembers", "21 4 * * *", now, JobResult.Ok, "counted the members of 10 of 10 servers"),
                Job("GuildNotificationJob.NotifyAllGuilds", "*/5 * * * *", now, JobResult.Ok),
                Job("HousekeepingJob.ApplyRetention", "30 4 * * *", now),
                Job("SteamTokenJob.RenewSteamToken", "0 12 * * *", now, JobResult.Ok)
            ]);
            await status.SaveJobRuns(
            [
                Run(1, "ArmorJob.AddColorArrays", now.AddMinutes(-1), JobResult.Ok),
                Run(2, "GuildMembersJob.CountMembers", now.AddMinutes(-1), JobResult.Ok, "counted the members of 10 of 10 servers"),
                Run(3, "GuildNotificationJob.NotifyAllGuilds", now.AddMinutes(-1), JobResult.Ok),
                Run(4, "SteamTokenJob.RenewSteamToken", now.AddMinutes(-1), JobResult.Ok)
            ]);

            var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            await status.SaveSamples(Enumerable.Range(1, 10).Select(back => new UptimeSample { minute = minute.AddMinutes(-back), discord_up = true, steam_up = true }).ToList());
        }

        // In the category Text Channels unless given otherwise, where the bot has every permission a post needs
        public static BotGuildChannel Channel(ulong id, string name, int position, string type = GuildChannelType.Text, string? category = "Text Channels",
            ulong? categoryId = 3900, int? categoryPosition = 0, bool view = true, bool send = true, bool embed = true) => new()
        {
            channel_id = id,
            name = name,
            type = type,
            category = category,
            category_id = categoryId,
            category_position = categoryPosition,
            position = position,
            can_view = view,
            can_send = send,
            can_embed = embed
        };

        public static BotGuild Server(ulong id, string name, uint? members = 120, bool administrator = false, bool available = true, string? icon = "a_0123456789abcdef0123456789abcdef") => new()
        {
            guild_id = id,
            name = name,
            icon_hash = icon,
            member_count = members,
            bot_joined_at = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            available = available,
            bot_administrator = administrator,
            bot_permissions = Permissions
        };

        // ServerId with a channel of each kind, one hidden, and settings for each status the server's page can't miss: its
        // armor channel works, its maps go to the hidden one, its lobbies are off and its announcements' channel isn't in
        // the copy. SettingsOnlyId has settings and no row.
        public static async Task ServersAsync(MariaDbFixture db)
        {
            var at = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

            await db.BotGuilds().SaveGuild(Server(Id(ServerId), "Test Server"),
            [
                Channel(3001, "general", 0),
                Channel(3002, "news", 0, GuildChannelType.Announcement, category: null, categoryId: null, categoryPosition: null),
                Channel(3003, "___hidden___", 1, view: false, send: false, embed: false)
            ], at, RunId, 1);

            await db.Guilds().SaveGuild(new Guild { guild_id = Id(ServerId), armor_channel_id = 3001, map_channel_id = 3003, lobby_channel_id = 0, announcements_channel_id = 999 });
            await db.Guilds().SaveGuild(new Guild { guild_id = Id(SettingsOnlyId), armor_channel_id = 4001, map_channel_id = 0, lobby_channel_id = 4002, announcements_channel_id = 0 });
        }
    }
}
