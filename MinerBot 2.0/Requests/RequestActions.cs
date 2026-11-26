using DataAccessLibrary;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Steam;
using MinerBot_2._0.Builders;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Posting;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using MySql.Data.MySqlClient;
using SteamWebAPIAccess;
using System.Diagnostics;
using System.Globalization;

namespace MinerBot_2._0.Requests
{
    // How an action ended, for its row: done, failed or unknown, in the bot's own words, a number when there's one, and the
    // extra the owner's message adds, such as the server's name. A sign-in alert is a direct message itself, so it says how
    // that went, or gives the message still going.
    public sealed record RequestOutcome(string State, string Result, ulong? Number = null, string? Extra = null)
    {
        public string? Dm { get; init; }
        public Task? DmGoing { get; init; }
    }

    // What the bot does for each kind of the dashboard's requests, and what it checks first, which a refusal then says. Each
    // action ends within its kind's limit: a call past it carries on in the background, and the outcome says it may or may
    // not have happened, apart from a lookup, which changes nothing, and so has failed, and a job's run, which has started,
    // and so is done while it still runs. A reconnect whose Steam isn't back in time is done too: its disconnect happened.
    // A restart is the request worker's.
    public sealed class RequestActions(DiscordSocketClient client, GuildWatcher watcher, IBotRequestData data, PostsPause pause, PresenceKeeper presence,
        JobHandler jobs, ISteamAPIAccess steam, IGetData getData, CacheReset caches, OwnerAlertService alerts, BotRecorder recorder, INotificationData posts, TimeProvider clock)
    {
        // A restart only from this long after a start, so it can't land in a deploy's readiness check: deploy/common.sh's
        // ready_bot, which fails a deploy whose bot restarts
        internal static readonly TimeSpan MinimumUptime = TimeSpan.FromMinutes(5);

        internal const string NotificationJob = "GuildNotificationJob.NotifyAllGuilds";

        static readonly Embed TestEmbed = new EmbedBuilderTemplate()
            .WithTitle(RequestTexts.TestTitle)
            .WithDescription(RequestTexts.TestDescription)
            .Build();

        // Shorter in tests: the waits, and how often a reconnect looks at Steam
        internal Func<RequestKind, TimeSpan> LimitOf = kind => kind.ActionLimit;
        internal TimeSpan SteamPoll = TimeSpan.FromSeconds(1);

        DateTime Now() => clock.GetUtcNow().UtcDateTime;

        // Check 10: what stops this kind now, from the bot's own state, or null
        public string? Refusal(RequestKind kind, BotRequestRow row, DateTime now) => kind.Word switch
        {
            RequestKinds.JobNotifications => NotificationRefusal() ?? JobRefusal(kind),
            RequestKinds.JobColors => JobRefusal(kind),
            RequestKinds.SteamRenew => steam.Status.State == SteamConnection.LoggedOn ? null : RequestTexts.NothingToRenew,
            RequestKinds.PostsPause => pause.Read() is { Paused: true } paused ? RequestTexts.AlreadyPaused(paused.Since) : null,
            RequestKinds.PostsResume => pause.Read().Paused == true ? null : RequestTexts.NotPaused,
            RequestKinds.SteamReconnect => steam.Status.State == SteamConnection.LoggedOn ? null : RequestTexts.NothingToReconnect,
            RequestKinds.GuildTestMessage => TestChannel(row, out _),
            RequestKinds.GuildRefresh => GuildRefusal(row.guild_id!.Value),
            RequestKinds.GuildLeave => client.GetGuild(row.guild_id!.Value) == null ? RequestTexts.NotInServer : null,
            RequestKinds.BotRestart => RestartRefusal(now),
            _ => null
        };

        // A run while posts are held would find new items and hold them anyway
        string? NotificationRefusal()
        {
            if (!pause.Holds(out var known))
                return null;

            return known ? RequestTexts.PausedNoRun : RequestTexts.PauseUnknownNoRun;
        }

        string? JobRefusal(RequestKind kind) => jobs.IsRunning(kind.Job!) ? RequestTexts.JobRunning(RequestTexts.JobName(kind)) : null;

        // Not in the first minutes of a run, and not while the notification job posts, which a stop would cut in half. A run
        // that was cut off doesn't count: a restart is the cure for one.
        string? RestartRefusal(DateTime now)
        {
            var up = now - recorder.StartedAt;

            if (up < MinimumUptime)
                return RequestTexts.RestartTooSoon(up, recorder.StartedAt + MinimumUptime);

            return jobs.Runs(NotificationJob) ? RequestTexts.RestartDuringNotifications : null;
        }

        // A server the bot can use now, from the cache, never Discord's API
        string? GuildRefusal(ulong guildId)
        {
            DiscordService.UsableGuild(client, guildId, out var problem);
            return Refusal(problem);
        }

        static string? Refusal(PostProblem problem) => problem switch
        {
            PostProblem.None => null,
            PostProblem.NotInServer => RequestTexts.NotInServer,
            PostProblem.ServerUnavailable => RequestTexts.ServerUnavailable,
            PostProblem.ServerNotSent => RequestTexts.ServerNotSent,
            _ => RequestTexts.NotATextChannel
        };

        // The notification job's channel rule, and only a text or announcement channel, with the three permissions every
        // armor, map and lobby post needs, as they're embeds. From the cache, so a missing permission costs no 403 from the
        // invalid-request budget Discord counts.
        string? TestChannel(BotRequestRow row, out SocketTextChannel? channel)
        {
            channel = DiscordService.PostChannel(client, row.guild_id!.Value, row.channel_id!.Value, out var problem);

            if (channel == null)
                return Refusal(problem);

            if (channel.ChannelType is not (ChannelType.Text or ChannelType.News))
            {
                channel = null;
                return RequestTexts.NotATextChannel;
            }

            var missing = DiscordService.MissingPermissions(channel.Guild.CurrentUser, channel, ChannelPermission.ViewChannel, ChannelPermission.SendMessages,
                ChannelPermission.EmbedLinks);

            if (missing.Count > 0)
            {
                channel = null;
                return RequestTexts.MissingPermissions(missing);
            }

            return null;
        }

        // The action, once the request is taken
        public Task<RequestOutcome> ActAsync(RequestKind kind, BotRequestRow row, CancellationToken stop) => kind.Word switch
        {
            RequestKinds.CachesClear => Task.FromResult(ClearCaches()),
            RequestKinds.JobNotifications or RequestKinds.JobColors => RunJobAsync(kind, row, stop),
            RequestKinds.SteamRenew => RenewAsync(kind, stop),
            RequestKinds.PostsPause => PauseAsync(kind, row, paused: true, stop),
            RequestKinds.PostsResume => PauseAsync(kind, row, paused: false, stop),
            RequestKinds.StatusSet => SetStatusAsync(kind, row, stop),
            RequestKinds.SteamReconnect => ReconnectAsync(kind, stop),
            RequestKinds.GuildTestMessage => SendTestMessageAsync(kind, row, stop),
            RequestKinds.GuildRefresh => RefreshAsync(kind, row, stop),
            RequestKinds.SteamLookup => LookUpAsync(kind, row, stop),
            RequestKinds.GuildLeave => LeaveAsync(kind, row, stop),
            RequestKinds.OwnerSigninAlert => TellAboutSigninAsync(kind, row, stop),
            _ => throw new ArgumentException($"{kind.Word} has no action here", nameof(kind))
        };

        static RequestOutcome Done(string result, ulong? number = null, string? extra = null) => new(RequestStates.Done, result, number, extra);
        static RequestOutcome Failed(string result, string? extra = null) => new(RequestStates.Failed, result, Extra: extra);
        static RequestOutcome Unknown(string result, string? extra = null) => new(RequestStates.Unknown, result, Extra: extra);

        static bool Stopping(Exception e, CancellationToken stop) => e is OperationCanceledException && stop.IsCancellationRequested;

        // The two cached reads, and nothing else: never what the notification job has seen, the cooldowns, the bot's
        // history, its copy of the servers or Steam's state
        RequestOutcome ClearCaches()
        {
            caches.Clear();
            return Done(RequestTexts.CachesCleared);
        }

        // Through JobHandler, which never runs one job twice at once, recorded as any run, with the request in its detail
        async Task<RequestOutcome> RunJobAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            var job = RequestTexts.JobName(kind);
            var run = await jobs.RunNowAsync(kind.Job!, row.id, LimitOf(kind), stop);

            return run.State switch
            {
                RunNowState.Ok => Done(RequestTexts.JobRan(job, run.Took, run.Detail)),
                RunNowState.Failed => Failed(RequestTexts.JobFailed(job, run.Took, run.Detail)),
                RunNowState.StillRunning => Done(RequestTexts.JobStillRunning(kind)),
                RunNowState.AlreadyRunning => Failed(RequestTexts.JobRunning(job)),
                _ => Failed(RequestTexts.JobsDidntStart)
            };
        }

        async Task<RequestOutcome> RenewAsync(RequestKind kind, CancellationToken stop)
        {
            var renewing = steam.RenewTokenAsync();
            TokenRenewal renewal;

            try
            {
                renewal = await renewing.WaitAsync(LimitOf(kind), stop);
            }
            catch (TimeoutException) when (!renewing.IsCompleted)
            {
                return Unknown(RequestTexts.RenewalUnanswered(kind));
            }

            return renewal.Outcome switch
            {
                TokenRenewalOutcome.Renewed => Done(RequestTexts.TokenRenewed(renewal.Expires)),
                TokenRenewalOutcome.Kept => Done(RequestTexts.TokenKept(renewal.Expires)),
                TokenRenewalOutcome.Failed => Failed(RequestTexts.TokenNotRenewed(renewal.FailureType, renewal.Expires)),
                TokenRenewalOutcome.NotSaved => Failed(RequestTexts.TokenNotSaved),
                _ => Failed(RequestTexts.NothingToRenew)
            };
        }

        // Saved before memory: a change MinerBot's database didn't take would otherwise hold or free posts until a restart
        // silently undid it. One whose answer didn't come, or was lost, is read back, which the request worker does at its
        // next tick.
        async Task<RequestOutcome> PauseAsync(RequestKind kind, BotRequestRow row, bool paused, CancellationToken stop)
        {
            var now = Now();
            var reason = paused ? row.argument : null;
            var extra = paused ? RequestTexts.ReasonExtra(reason) : null;

            try
            {
                await data.SavePause(paused, reason, now, row.actor_id, row.id, now).WaitAsync(LimitOf(kind), stop);
            }
            catch (TimeoutException)
            {
                return Unknown(RequestTexts.ChangeUnconfirmed, extra);
            }
            catch (Exception e) when (!Stopping(e, stop))
            {
                return SaveRefused(e) ? Failed(RequestTexts.PauseNotSaved(paused), extra) : Unknown(RequestTexts.ChangeAnswerLost, extra);
            }

            pause.Load(paused, reason, now);
            recorder.Record(paused ? BotEvents.PostsPaused : BotEvents.PostsResumed, RequestTexts.ByRequest(row.id));

            return Done(paused ? RequestTexts.Paused : RequestTexts.Resumed, extra: extra);
        }

        // The same order: the status changes only once MinerBot's database has the text, and is sent again at every connection
        async Task<RequestOutcome> SetStatusAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            var now = Now();
            var text = row.argument;
            var extra = RequestTexts.StatusExtra(text);

            try
            {
                await data.SaveStatus(text, now, row.actor_id, row.id, now).WaitAsync(LimitOf(kind), stop);
            }
            catch (TimeoutException)
            {
                return Unknown(RequestTexts.ChangeUnconfirmed, extra);
            }
            catch (Exception e) when (!Stopping(e, stop))
            {
                return SaveRefused(e) ? Failed(RequestTexts.StatusNotSaved, extra) : Unknown(RequestTexts.ChangeAnswerLost, extra);
            }

            await presence.SetAsync(text);

            return Done(text == null ? RequestTexts.StatusReset : RequestTexts.StatusSet, extra: extra);
        }

        // MinerBot's database refused the change: the server answered with an error of its own, which it gives instead of
        // making the change, or MySql.Data's 1042, a server it never reached. Any other failure, such as a connection lost
        // after the statement went out, which MySql.Data reports without a server's error number, may come after the server
        // made it.
        static bool SaveRefused(Exception e) => e is MySqlException { Number: (>= 1000 and < 2000) or >= 3000 };

        // The pump reconnects by its own rules, quickly: the request waits for it to log on again
        async Task<RequestOutcome> ReconnectAsync(RequestKind kind, CancellationToken stop)
        {
            var before = steam.Status;
            var requestedAt = clock.GetUtcNow();
            var waited = Stopwatch.StartNew();

            switch (steam.RequestReconnect())
            {
                case SteamReconnect.NotLoggedOn:
                    return Failed(RequestTexts.NothingToReconnect);
                case SteamReconnect.SigningIn:
                    return Failed(RequestTexts.SteamSigningIn);
            }

            var limit = LimitOf(kind);

            while (waited.Elapsed < limit)
            {
                await Task.Delay(SteamPoll, stop);

                var status = steam.Status;

                if (status.State == SteamConnection.LoggedOn && status.Since > before.Since)
                    return Done(RequestTexts.SteamBack(status.Since - requestedAt));
            }

            return Done(RequestTexts.SteamNotBack(kind));
        }

        // A fixed, silent embed that pings nobody, so the dashboard can't be used to post text, and the test shows whether an
        // armor, map or lobby post could arrive. The cache is checked again, since it may have changed since the refusals.
        async Task<RequestOutcome> SendTestMessageAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            if (TestChannel(row, out var channel) is { } refusal)
                return Failed(refusal);

            var sending = channel!.SendMessageAsync(embed: TestEmbed, allowedMentions: AllowedMentions.None, flags: MessageFlags.SuppressNotification);

            try
            {
                var message = await sending.WaitAsync(LimitOf(kind), stop);
                await EndWaitsAsync(row.guild_id!.Value, channel.Id);
                return Done(RequestTexts.TestMessageSent, message.Id);
            }
            catch (HttpException e)
            {
                return Failed(RequestTexts.TestMessageRefused(Code(e)));
            }
            catch (Exception e) when (!Stopping(e, stop))
            {
                return Unknown(RequestTexts.TestMessageUnanswered(kind));
            }
        }

        // From the bot's cache: nothing can make Discord send one server again
        async Task<RequestOutcome> RefreshAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            if (GuildRefusal(row.guild_id!.Value) is { } refusal)
                return Failed(refusal);

            var rewrite = watcher.RequestRewrite(row.guild_id!.Value);

            try
            {
                var outcome = await rewrite.WaitAsync(LimitOf(kind), stop);

                if (outcome.State == RewriteState.Written)
                    await EndWaitsAsync(row.guild_id!.Value, null);

                return outcome.State switch
                {
                    RewriteState.Written => Done(RequestTexts.Rewritten(outcome.At, outcome.Channels)),
                    RewriteState.Failed => Failed(RequestTexts.CopyNotTaken(outcome.Failure)),
                    _ => Failed(RequestTexts.LeftBeforeRewrite)
                };
            }
            catch (TimeoutException) when (!rewrite.IsCompleted)
            {
                return Unknown(RequestTexts.CopyNotRewritten(kind));
            }
        }

        // A test message that went to a channel, or the server's copy written again, ends the waits of its posts that failed
        // there, or in any of its channels, so the next notification run tries them at once. MinerBot's database not taking it
        // costs only the wait.
        async Task EndWaitsAsync(ulong guildId, ulong? channelId)
        {
            try
            {
                await posts.EndWaitsAsync(guildId, channelId, Now());
            }
            catch (Exception e)
            {
                Console.WriteLine(PostingTexts.WaitsNotEnded(guildId, FailureLog.Describe(e)));
            }
        }

        // The name passed the ASCII rule twice, as the dashboard signed it and as the bot verified it, so Steam is never asked
        // about a name such as one with the Kelvin sign, which the shared link pattern lets through
        async Task<RequestOutcome> LookUpAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            ulong? id;

            try
            {
                id = await getData.ResolveVanityURL(row.argument!).WaitAsync(LimitOf(kind), stop);
            }
            catch (Exception e) when (!Stopping(e, stop))
            {
                return Failed(RequestTexts.LookupFailed(e.GetType().Name));
            }

            if (id == null)
                return Done(RequestTexts.NoSuchProfile);

            var account = SteamIds.Parse(id.Value.ToString(CultureInfo.InvariantCulture));

            return account.Kind == SteamIdKind.AccountNumber ? Done(RequestTexts.LookedUp(account.AccountNumber), account.AccountNumber) : Failed(RequestTexts.NotAPlayer);
        }

        // Discord's GUILD_DELETE then raises LeftGuild, which records the leave with this request in the server's history,
        // and deletes the website's channel row, as when a server removes the bot
        async Task<RequestOutcome> LeaveAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            var guildId = row.guild_id!.Value;

            if (client.GetGuild(guildId) is not { } guild)
                return Failed(RequestTexts.NotInServer);

            // Before the call, which takes the server out of the cache
            var extra = RequestTexts.ServerExtra(guild.Name, guildId);

            watcher.ExpectLeave(guildId, RequestTexts.LeftOnRequest(row.id));

            var leaving = guild.LeaveAsync();

            try
            {
                await leaving.WaitAsync(LimitOf(kind), stop);
                return Done(RequestTexts.Left, extra: extra);
            }
            catch (HttpException e)
            {
                watcher.ForgetLeave(guildId);
                return Failed(RequestTexts.LeaveRefused(Code(e)), extra);
            }
            catch (Exception e) when (!Stopping(e, stop))
            {
                return Unknown(RequestTexts.LeaveUnanswered(kind), extra);
            }
        }

        // The message is the action: who signed in is who asked, and when is when they asked, so no address can reach it
        async Task<RequestOutcome> TellAboutSigninAsync(RequestKind kind, BotRequestRow row, CancellationToken stop)
        {
            var sending = alerts.SendAsync(RequestTexts.SigninMessage(row), RequestTexts.SigninRecord(row.id));

            try
            {
                await sending.WaitAsync(LimitOf(kind), stop);
                return Done(RequestTexts.SigninSent) with { Dm = RequestDmStates.Sent };
            }
            catch (TimeoutException) when (!sending.IsCompleted)
            {
                return Unknown(RequestTexts.SigninUnanswered(kind)) with { DmGoing = sending };
            }
            catch (Exception e) when (!Stopping(e, stop))
            {
                return Failed(RequestTexts.SigninFailed(FailureLog.Describe(e))) with { Dm = RequestDmStates.Failed };
            }
        }

        // Discord's own code, such as 50013 for missing permissions, or else the HTTP status, as for Discord's own trouble,
        // whose answer's code is 0
        static int Code(HttpException e) => FailureLog.DiscordCode(e);
    }
}
