using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using System.Globalization;

namespace MinerBot_2._0.Posting
{
    // What the look in the channels' histories left: the messages still not settled, each of which holds its server's kind
    // back, and the server's kinds whose message it found sent, which ended their wait
    public sealed record VerifiedDeliveries(IReadOnlyList<DeliveryRow> StillOpen, IReadOnlySet<(ulong GuildId, string Source)> Sent);

    // The look in a channel's history for a message whose fate the bot doesn't know: Discord didn't answer its send, or the
    // process that sent it stopped before it wrote the answer down. At each run, before any send, it takes the messages in
    // doubt and another process's still sending, once they're a minute old, so a message Discord made late is there, and
    // this process's own still sending after 5 minutes with no settle of it waiting in memory, which only a fault leaves.
    // Where the cache gives the bot View Channel and Read Message History, it reads the channel's messages from 5 s before
    // the attempt, a page of 100 at a time and at most three, until it has read 2 minutes past the attempt:
    // - a message the bot posted then, that no other row holds and that's of the kind (armor's title, the lobbies' title, or
    //   the link of one of the maps it carries): it went, with that ID
    // - none, with the read past those 2 minutes or at the channel's end: it wasn't made, so its items wait again, and this
    //   run sends them
    // - the bot can't look (a permission missing, the channel or the server gone, Discord refusing the read, a channel too
    //   busy to read that far): a look that couldn't tell. At the third, or an hour after the attempt, it counts as sent, so
    //   it's never sent twice, and the posts.unconfirmed event says it may not have arrived.
    // A read Discord doesn't answer within its own limit counts no look, and the rest wait for the next run, as does
    // MinerBot's database not answering; the messages of a server in an outage wait too, counting no look. An hour after the
    // attempt such a read counts the message as sent, unless, when the look began, Discord had answered none of the posts'
    // requests since the attempt, as in an outage of Discord or of the bot's connection to it: then the message waits on,
    // up to its kind's time limit, so the first look Discord answers finds it or sends it once. Past the hour the run's
    // second read with no answer ends the look, whether its message waits or counts as sent, so such reads cost at most two
    // requests' limits a run. One channel that alone never answers can't be told from an outage until Discord answers a
    // request the bot makes after the attempt: when its message was the bot's last post, before a spell with nothing to
    // post or right after a start, it waits, its channel read at each run, until Discord answers the bot's next post or
    // read anywhere, and counts as sent at the run after. So does a channel whose part of Discord is still down when the
    // rest is back. Discord's text is never kept: only its numbers, in the log.
    public sealed class DeliveryVerifier(DiscordSocketClient client, INotificationData store, PendingSettlements pending, PostingGate gate, PostingProcess process,
        BotRecorder recorder, string mapLink, ulong runId, DateTime now, TimeProvider clock)
    {
        enum Found
        {
            Sent,
            NotThere,
            CantLook,
            NoAnswer,
            Waits
        }

        sealed record Look(Found Outcome, ulong MessageId = 0, string Because = "");

        DateTime Now() => clock.GetUtcNow().UtcDateTime;

        public async Task<VerifiedDeliveries> ResolveAsync(IReadOnlyList<DeliveryRow> unsettled)
        {
            var stillOpen = new List<DeliveryRow>();
            var sent = new HashSet<(ulong, string)>();
            var taken = new HashSet<ulong>();
            var stopped = false;
            var unansweredPastTheHour = false;

            // Discord's last answer as the look begins, which it goes by past a message's hour: an answer to one of its own
            // reads counts from the next run, so in the first run after an outage a read that misses once, after another
            // channel's was answered, still waits for the next run's look
            var answered = process.LastAnswered;

            foreach (var row in unsettled)
            {
                if (stopped || !gate.IsOpen || !Due(row))
                {
                    stillOpen.Add(row);
                    continue;
                }

                Look look;

                try
                {
                    look = await LookAsync(row, taken);
                }
                catch (Exception e)
                {
                    // MinerBot's database didn't give the messages its rows hold, or the items this one carries
                    Console.WriteLine(PostingTexts.CheckFailed(row.source, row.channel_id, row.guild_id, FailureLog.Describe(e)));
                    stillOpen.Add(row);
                    stopped = true;
                    continue;
                }

                if (look.Outcome == Found.NoAnswer && now - row.attempted_at < PostingLimits.LookFor)
                {
                    Console.WriteLine(PostingTexts.ReadUnanswered(row.channel_id, row.guild_id, look.Because));
                    stillOpen.Add(row);
                    stopped = true;
                    continue;
                }

                // Past its hour, a read with no answer keeps the message waiting while Discord had answered none of the posts'
                // requests since its attempt when the look began, as in an outage of any form, up to its kind's time limit.
                // The run's first such read leaves the next message's read to tell an outage from this channel alone, whose
                // message then counts as sent at the next run; a second ends the look for the run, whether its message waits
                // or counts as sent, so a run spends at most two requests' limits on them. With no other message to read and
                // nothing posted since the attempt, nothing tells them apart, and the message waits for the next post or read
                // Discord answers.
                if (look.Outcome == Found.NoAnswer)
                {
                    stopped = unansweredPastTheHour;
                    unansweredPastTheHour = true;

                    if (!PostingProcess.AnsweredSince(answered, row.attempted_at) && now - row.attempted_at < PostingLimits.OutageLookFor(row.source))
                    {
                        Console.WriteLine(PostingTexts.ReadUnansweredInOutage(row.source, row.channel_id, row.guild_id, look.Because));
                        stillOpen.Add(row);
                        continue;
                    }
                }

                if (look.Outcome == Found.Waits)
                {
                    stillOpen.Add(row);
                    continue;
                }

                if (look.Outcome is Found.CantLook && row.checks + 1 < PostingLimits.Looks && now - row.attempted_at < PostingLimits.LookFor)
                {
                    Console.WriteLine(PostingTexts.CouldntCheck(row.source, row.channel_id, row.guild_id, look.Because, row.checks + 1));
                    stillOpen.Add(row);

                    try
                    {
                        await store.MarkCheckedAsync(row.attempt_key, (byte)(row.checks + 1));
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(PostingTexts.CheckFailed(row.source, row.channel_id, row.guild_id, FailureLog.Describe(e)));
                        stopped = true;
                    }

                    continue;
                }

                var settlement = look.Outcome switch
                {
                    Found.Sent => new Settlement { AttemptKey = row.attempt_key, Outcome = DeliveryState.Sent, At = Now(), MessageId = look.MessageId },
                    Found.NotThere => new Settlement { AttemptKey = row.attempt_key, Outcome = DeliveryState.Failed, At = Now(), Reason = DeliveryReasons.NotSent },
                    _ => new Settlement { AttemptKey = row.attempt_key, Outcome = DeliveryState.Unconfirmed, At = Now() }
                };

                bool settled;

                try
                {
                    settled = await store.SettleAsync(settlement);
                }
                catch (Exception e)
                {
                    // It's still open in MinerBot's database, so it holds its kind back until the next run applies this
                    Console.WriteLine(PostingTexts.CheckFailed(row.source, row.channel_id, row.guild_id, FailureLog.Describe(e)));
                    pending.Add(settlement);
                    stillOpen.Add(row);
                    stopped = true;
                    continue;
                }

                // Settled meanwhile, by another process or what waited in memory
                if (!settled)
                    continue;

                switch (look.Outcome)
                {
                    case Found.Sent:
                        Console.WriteLine(PostingTexts.FoundSent(row.source, row.channel_id, row.guild_id));
                        taken.Add(look.MessageId);
                        sent.Add((row.guild_id, row.source));
                        break;

                    case Found.NotThere:
                        Console.WriteLine(PostingTexts.NotThere(row.source, row.channel_id, row.guild_id));
                        break;

                    default:
                        Console.WriteLine(PostingTexts.Unconfirmed(row.source, row.channel_id, row.guild_id, look.Because));
                        recorder.Record(BotEvents.PostsUnconfirmed, PostingTexts.UnconfirmedEvent(row.source, row.guild_id, row.channel_id, row.attempted_at));
                        break;
                }
            }

            return new VerifiedDeliveries(stillOpen, sent);
        }

        // Whether this run looks for the message: not while a settle of it waits in memory, and not while it may still be
        // under way
        bool Due(DeliveryRow row)
        {
            if (pending.Holds(row.attempt_key))
                return false;

            var age = now - row.attempted_at;

            return row.state == DeliveryState.Sending && row.run_id == runId ? age >= PostingLimits.StaleSending : age >= PostingLimits.CheckAfter;
        }

        async Task<Look> LookAsync(DeliveryRow row, HashSet<ulong> taken)
        {
            // An outage isn't the bot unable to look: the server's messages wait for it to come back
            if (DiscordService.UsableGuild(client, row.guild_id, out var problem) is not { } guild)
                return problem == PostProblem.NotInServer ? new Look(Found.CantLook, Because: PostingTexts.NotInServer) : new Look(Found.Waits);

            if (guild.GetTextChannel(row.channel_id) is not { } channel)
                return new Look(Found.CantLook, Because: PostingTexts.ChannelNotInServer);

            if (guild.CurrentUser is not { } bot)
                return new Look(Found.Waits);

            var permissions = bot.GetPermissions(channel);
            var missing = (permissions.ViewChannel ? 0 : DeliveryReasons.ViewChannel) | (permissions.ReadMessageHistory ? 0 : DeliveryReasons.ReadMessageHistory);

            if (missing != 0)
                return new Look(Found.CantLook, Because: PostingTexts.Missing(missing));

            var from = row.attempted_at - PostingLimits.HistoryBefore;
            var until = row.attempted_at + PostingLimits.HistoryAfter;

            // A message can be made up to HistoryAfter after its attempt, after a slow claim or send, so one made in this
            // window may be held by a row tried that long before the window opens
            var held = (await store.ListMessageIdsAsync(row.guild_id, row.channel_id, from - PostingLimits.HistoryAfter)).ToHashSet();

            var mapLinks = row.source == PostSource.Maps
                ? (await store.ListCarriedAsync(row.id)).Select(id => mapLink + id.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal)
                : [];

            var after = SnowflakeUtils.ToSnowflake(new DateTimeOffset(from, TimeSpan.Zero));

            for (var page = 1; page <= PostingLimits.HistoryPages; page++)
            {
                List<IMessage> messages;

                try
                {
                    using var limit = gate.Limit();
                    messages = (await channel.GetMessagesAsync(after, Direction.After, PostingLimits.HistoryPage, Deliverer.SendOptions(limit.Token)).FlattenAsync()).ToList();
                }
                catch (HttpException e) when ((int)e.HttpCode is >= 400 and < 500 and not 401 and not 429)
                {
                    process.Answered(Now());
                    return new Look(Found.CantLook, Because: PostingTexts.ReadRefused((int)e.HttpCode, DeliveryReasons.CodeOf((int?)e.DiscordCode)));
                }
                catch (HttpException e)
                {
                    return new Look(Found.NoAnswer, Because: PostingTexts.Answer((int)e.HttpCode, DeliveryReasons.CodeOf((int?)e.DiscordCode)));
                }
                catch (Exception) when (gate.Abandon.IsCancellationRequested)
                {
                    // The bot's own stop ended the read, which says nothing of Discord's answer, so it counts no look and
                    // never the hour: the message waits for the next start's look
                    return new Look(Found.Waits);
                }
                catch (Exception e)
                {
                    return new Look(Found.NoAnswer, Because: e.GetType().Name);
                }

                process.Answered(Now());

                var found = messages
                    .Where(message => message.Author.Id == bot.Id && message.Type == MessageType.Default && !held.Contains(message.Id) && !taken.Contains(message.Id)
                        && message.CreatedAt.UtcDateTime >= from && message.CreatedAt.UtcDateTime < until && IsOfKind(message, row.source, mapLinks))
                    .OrderBy(message => message.Id)
                    .FirstOrDefault();

                if (found != null)
                    return new Look(Found.Sent, found.Id);

                // The channel's end, or past the time the message could have been made in
                if (messages.Count < PostingLimits.HistoryPage || messages.Max(message => message.CreatedAt.UtcDateTime) >= until)
                    return new Look(Found.NotThere);

                after = messages.Max(message => message.Id);
            }

            return new Look(Found.CantLook, Because: PostingTexts.TooBusy);
        }

        // The bot's own post of the kind, by what only that post has: a slash command's answer is another type of message
        static bool IsOfKind(IMessage message, string source, IReadOnlySet<string> mapLinks) => source switch
        {
            PostSource.Armor => message.Embeds.Any(embed => embed.Title == ArmorService.NewArmorTitle),
            PostSource.Lobbies => message.Embeds.Any(embed => embed.Title == MessagePlan.LobbiesTitle),
            _ => message.Embeds.Any(embed => embed.Url != null && mapLinks.Contains(embed.Url))
        };
    }
}
