using DataAccessLibrary.Guilds;
using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Models;
using MinerBot_2._0.Status;
using System.Diagnostics;
using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    // Each server's member count, once a day, for the dashboard's trend. Discord.Net's count is right only just after
    // Discord sends a server, as the bot has no members intent, so this asks Discord's API for each server's approximate
    // count, one server at a time with a short pause between them, and writes it to the server's row and the day's row. A
    // request that fails leaves its server for the next day, and the run's detail gives the counts. A write that fails ends
    // the run, as the rest would fail too. Without BotConnection it does nothing: the bot said so once as it started.
    public class GuildMembersJob(DiscordSocketClient discord, IBotGuildData guilds, TimeProvider clock, IConfiguration configuration, ILogger<GuildMembersJob> logger)
    {
        // All three shorter in tests
        internal TimeSpan Pause = TimeSpan.FromMilliseconds(250);

        // A request that takes longer has failed
        internal TimeSpan RequestLimit = TimeSpan.FromSeconds(15);

        // After this no request starts, so the job ends well inside JobHandler's limit of 270 seconds even when the last
        // request and its write each take their whole limit
        internal TimeSpan TimeBudget = TimeSpan.FromSeconds(200);

        [Job("21 4 * * *")] // 04:21 US Eastern every day, a minute no other job runs at, so it's done before the 04:25 notifications
        public async Task<string> CountMembers()
        {
            if (StatusWriter.BotConnectionProblem(configuration) is { } problem)
                return $"skipped, because ConnectionStrings:BotConnection {problem}";

            var servers = discord.Guilds.Select(guild => guild.Id).Order().ToList();
            var budget = Stopwatch.StartNew();
            var problems = new List<string>();
            int counted = 0, left = 0;

            for (var i = 0; i < servers.Count; i++)
            {
                if (i > 0)
                {
                    if (budget.Elapsed >= TimeBudget)
                    {
                        left = servers.Count - i;
                        break;
                    }

                    await Task.Delay(Pause, clock);
                }

                uint? count;

                try
                {
                    count = await CountAsync(servers[i]);
                }
                catch (Exception e)
                {
                    // The type only, as Discord's message is Discord's
                    problems.Add(FailureLog.Describe(e));
                    continue;
                }

                if (count is not { } members)
                {
                    problems.Add("no count");
                    continue;
                }

                var now = clock.GetUtcNow().UtcDateTime;

                try
                {
                    await guilds.SaveMemberCount(servers[i], members, now.Date, now);
                }
                catch (Exception e)
                {
                    throw new JobFailedException($"{Detail()}; failed: saving them ({FailureLog.Describe(e)})");
                }

                counted++;
            }

            if (problems.Count > 0)
                logger.LogWarning("Some of the daily member count's requests failed ({Problems}), and their servers are left for the next day", Problems());

            return Detail();

            string Detail()
            {
                var detail = string.Create(CultureInfo.InvariantCulture, $"counted the members of {counted} of {Count(servers.Count, "server")}");

                if (problems.Count > 0)
                    detail += string.Create(CultureInfo.InvariantCulture, $", and {Count(problems.Count, "request")} failed ({Problems()})");

                if (left > 0)
                    detail += string.Create(CultureInfo.InvariantCulture, $"; {left} left for the next run");

                return detail;
            }

            string Problems() => string.Join(", ", problems.Distinct());
        }

        // Discord's approximate count, or null when it gave none, as for a server the bot is no longer in
        async Task<uint?> CountAsync(ulong guildId)
        {
            // Not disposed, because a request that was given up on may still read it
            var limit = new CancellationTokenSource(RequestLimit);
            var guild = await discord.Rest.GetGuildAsync(guildId, withCounts: true, new RequestOptions { CancelToken = limit.Token }).WaitAsync(RequestLimit);

            return guild?.ApproximateMemberCount is int count && count >= 0 ? (uint)count : null;
        }

        static string Count(int count, string one) => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : one + "s")}");
    }
}
