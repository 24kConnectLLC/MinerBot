using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Discord.WebSocket;
using MinerBot_2._0.Models;
using MinerBot_2._0.Requests;
using MinerBot_2._0.Services;
using MinerBot_2._0.Status;
using Steam.Models;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Posting
{
    // One notification run. It finds what's new and queues it for every server owed it, committed even while posts are
    // paused, then delivers what waits, server by server and kind by kind, so a restart loses nothing and a crash doubles
    // nothing. Step by step:
    //  1. one time for the whole run; the approved armor and the open lobbies, whose counts are the lobby sample
    //  2. Discord: while it has sent none of the bot's servers, nothing can be checked against its server
    //  3. what earlier runs sent and MinerBot's database didn't take, written first
    //  4. what MinerBot's database holds: the baselines, the switches, the statuses, what waits, the messages not settled
    //     yet and the reads' items, in one read; then, once a process, whether an older bot posted since this one last checked
    //  5. the website's channel rows, which with the switches and the cache say which servers are owed each kind
    //  6. Steam's latest maps, five while the last good read is recent and twenty after a gap or at a baseline
    //  7. what's new, by the Detector, saved in one transaction, and an event for each baseline
    //  8. paused, or not known yet whether: what was found is queued, and waits, and what waits for a server that's off now
    //     is dropped, as the delivery would
    //  9. the statuses of kinds that are off now stop failing; the look in the channels' histories for the messages whose
    //     fate isn't known; the queue as committed; the delivery
    // Without MinerBot's database it can't load, so posts wait, and the run fails saying why; a claim, a settle, a refusal or
    // a drop it doesn't take stops the delivery, and the run's detail says why. Every run's detail says what it did; an older
    // bot's never do, which is how a run of one is told apart.
    public sealed class PostingRun(DiscordSocketClient client, ArmorService armorService, SteamAPIService steamService, DiscordService discordService,
        IGetData steam, INotificationData store, PostsPause pause, BotRecorder recorder, PostingProcess process, PendingSettlements pending, PostingGate gate, TimeProvider clock)
    {
        public async Task<string> RunAsync()
        {
            // To the millisecond, as MinerBot's database keeps it, so what this run writes and compares is the same time
            var started = clock.GetUtcNow().UtcDateTime;
            var now = started.AddTicks(-(started.Ticks % TimeSpan.TicksPerMillisecond));
            var (armor, armorProblem) = await ReadArmorAsync();
            var lobbies = await ReadLobbiesAsync();

            // The analytics' count of the open lobbies, from this read, before anything can end the run: a paused run, one
            // before Discord is ready and one MinerBot's database fails sample too. The recorder keeps it until it's written.
            recorder.SampleLobbies(LobbySamples.Of(now, lobbies));

            // Before the gateway's first READY since the start Discord.Net has none of the bot's servers, and from a READY, at
            // a start or a new session, nothing of them but their IDs until they arrive. The record is read only once Discord
            // has sent them, so nothing is found and queued against servers the bot can't see yet.
            if (client.CurrentUser == null || (client.Guilds.Count > 0 && client.Guilds.All(guild => guild.Name == null)))
                return PostingTexts.DiscordNotReady;

            // Before anything is read, so the load sees how those messages ended
            try
            {
                await pending.ApplyAsync(store);
            }
            catch (Exception e)
            {
                throw new JobFailedException(PostingTexts.BacklogNotTaken(FailureLog.Describe(e)));
            }

            PostingState state;

            try
            {
                state = await store.LoadAsync(new PostingLoad
                {
                    ArmorIds = armor?.Where(item => item.ID > 0).Select(item => (ulong)item.ID).Distinct().ToList() ?? [],
                    LobbyIds = lobbies?.Where(lobby => lobby.LobbyId != 0).Select(lobby => lobby.LobbyId).Distinct().ToList() ?? []
                });
            }
            catch (Exception e)
            {
                // The switches come with it, so every post of the run waits: no row means on, and a read that failed must never
                // read as on
                throw new JobFailedException(PostingTexts.DatabaseDidntAnswer(FailureLog.Describe(e)));
            }

            var olderBot = await OlderBotAsync(state.Baselines);

            List<Guild> channelRows;

            try
            {
                channelRows = (await discordService.GetAllGuilds()).ToList();
            }
            catch (Exception e)
            {
                throw new JobFailedException(PostingTexts.WebsiteDidntAnswer(FailureLog.Describe(e)));
            }

            var check = GroupCheck.Of(channelRows, state.Switches, client);

            var mapsBaseline = state.Baselines.FirstOrDefault(row => row.source == PostSource.Maps);
            var maps = await ReadMapsAsync(PostingLimits.MapReadDepth(now, mapsBaseline?.checked_at, mapsBaseline == null || olderBot.ContainsKey(PostSource.Maps)));
            var seen = state.Seen.ToList();
            IReadOnlyList<QueueRow> hidden = [];

            if (maps != null && Detector.Dated(maps).Select(map => map.PublishedFileId).ToList() is { Count: > 0 } mapIds)
            {
                seen.AddRange(await DatabaseAsync(() => store.ListSeenAsync(PostSource.Maps, mapIds)));
                hidden = await DatabaseAsync(() => store.ListHiddenAsync(mapIds));
            }

            var reads = new PostingReads(armor, maps, lobbies);

            var found = Detector.Detect(new DetectorInput
            {
                Now = now,
                RunId = recorder.RunId,
                Reads = reads,
                Baselines = state.Baselines,
                Seen = seen,
                Withdrawn = state.Withdrawn,
                Hidden = hidden,
                Owed = check.Owed(),
                OlderBot = olderBot
            });

            if (!found.Detection.IsEmpty)
            {
                try
                {
                    await store.SaveDetectionAsync(found.Detection);
                }
                catch (Exception e)
                {
                    throw new JobFailedException(PostingTexts.FindingNotSaved(FailureLog.Describe(e)));
                }
            }

            process.Rebaselined(found.Baselines.Where(baseline => baseline.Reason == BaselineReason.OlderBot).Select(baseline => baseline.Source));

            foreach (var baseline in found.Baselines)
                recorder.Record(BotEvents.PostsBaseline, PostingTexts.BaselineEvent(baseline));

            // Paused from the dashboard, or not known yet: what this run found is queued, so it waits through a restart too,
            // and goes out at the first run after the resume. What waits for a server that's off now is dropped all the same,
            // so a kind switched off and on again during the pause gets nothing found before the off.
            if (pause.Holds(out var known))
            {
                var dropped = await DropOffKindsAsync(state.Open, check, now);
                return known ? Paused(state.Open.Where(row => !dropped.Contains(row.id)), state, hidden, found.Detection) : PostingTexts.PauseUnknown;
            }

            await ClearOffKindsAsync(state.Statuses, check, now);

            // The messages whose fate isn't known, from the channels' histories, before the queue is read, so a message that
            // wasn't made is sent again in this run; what's still not settled holds its server's kind back, and a kind whose
            // message was found sent stops waiting
            var verified = await new DeliveryVerifier(client, store, pending, gate, process, recorder, steamService.WorkshopItemDetailsURI, recorder.RunId, now, clock)
                .ResolveAsync(state.Unsettled);

            var open = await DatabaseAsync(() => store.ListOpenAsync());
            var unsettled = verified.StillOpen.Select(row => (row.guild_id, row.source)).ToHashSet();
            var statuses = state.Statuses.Where(row => !verified.Sent.Contains((row.guild_id, row.source))).ToDictionary(row => (row.guild_id, row.source));
            var plan = new MessagePlan(steamService, steam, reads, open);
            var report = await new Deliverer(client, store, plan, check, unsettled, statuses, gate, pending, process, recorder.RunId, now, clock).DeliverAsync(open);

            return RunSummary.Of(found.Baselines, armorProblem, report,
                foundNew: found.Detection.Queue.Count + found.Detection.ReopenArmor.Count + found.Detection.ReopenMaps.Count > 0);
        }

        // An empty list says the read failed, as it always has, so it's never a baseline. The type and MySQL's number go in
        // the run's detail, so one row the read can't take, which stops armor posts, shows on the Bot page.
        async Task<(List<ArmorStyle>? Armor, string? Problem)> ReadArmorAsync()
        {
            try
            {
                var armor = await armorService.GetArmorList();
                return (armor.Count == 0 ? null : armor, null);
            }
            catch (Exception e)
            {
                Console.WriteLine(PostingTexts.ReadFailed("the armor list", e));
                return (null, FailureLog.Describe(e));
            }
        }

        // GetData returns an empty list when Steam fails
        async Task<List<PublishedFileDetailsModel>?> ReadMapsAsync(int depth)
        {
            try
            {
                var maps = await steamService.GetLatestWorkshopItems(depth);
                return maps.Count == 0 ? null : maps;
            }
            catch (Exception e)
            {
                Console.WriteLine(PostingTexts.ReadFailed("the latest maps", e));
                return null;
            }
        }

        // An answer with no lobbies is a good read; no answer isn't
        async Task<List<LobbyInfo>?> ReadLobbiesAsync()
        {
            try
            {
                var lobbies = await steamService.GetLobbyList();
                return lobbies.Available ? lobbies.Lobbies : null;
            }
            catch (Exception e)
            {
                Console.WriteLine(PostingTexts.ReadFailed("the lobby list", e));
                return null;
            }
        }

        // Once a process, at its first run that loads the record: the sources for which an older bot, which keeps no record,
        // finished a run since this bot last checked them, as after ./rollback.sh bot and a forward deploy. Each takes a new
        // baseline at its next good read. A source with no baseline yet takes its first anyway.
        async Task<IReadOnlyDictionary<string, DateTime>> OlderBotAsync(IReadOnlyList<BaselineRow> baselines)
        {
            if (!process.LookedForOlderBot)
            {
                var found = new Dictionary<string, DateTime>(StringComparer.Ordinal);

                foreach (var row in baselines.Where(row => PostSource.IsPosted(row.source)))
                {
                    if (await DatabaseAsync(() => store.CountOlderBotRunsAsync(RequestActions.NotificationJob, row.checked_at)) > 0)
                        found[row.source] = row.checked_at;
                }

                process.FoundOlderBot(found);
            }

            return process.OlderBot;
        }

        // A kind that's off for a server now stops failing and waiting, and keeps its last failure as history, so the pages
        // never show a kind as failing that nothing is posted for
        async Task ClearOffKindsAsync(IReadOnlyList<DeliveryStatusRow> statuses, GroupCheck check, DateTime now)
        {
            foreach (var status in statuses.Where(row => row.failing_since != null || row.failures > 0 || row.next_try_at != null || row.waiting_on != null))
            {
                if (!PostSource.IsPosted(status.source) || check.OffReason(status.guild_id, status.source) == null)
                    continue;

                try
                {
                    await store.ClearFailingAsync(status.guild_id, status.source, now);
                }
                catch (Exception e)
                {
                    Console.WriteLine(PostingTexts.StatusNotCleared(status.source, status.guild_id, FailureLog.Describe(e)));
                }
            }
        }

        // What a paused run does of the delivery: the pending rows of a server's kind that's off now are dropped with the
        // reason, as the Deliverer drops them first, so what was found before the off never goes once the kind is on again.
        // The rows it dropped; a drop MinerBot's database didn't take leaves the rest for the next run.
        async Task<IReadOnlySet<ulong>> DropOffKindsAsync(IReadOnlyList<QueueRow> open, GroupCheck check, DateTime now)
        {
            var dropped = new HashSet<ulong>();

            foreach (var group in open.GroupBy(row => (row.guild_id, row.source)))
            {
                if (check.OffReason(group.Key.guild_id, group.Key.source) is not { } off)
                    continue;

                var ids = group.Select(row => row.id).ToList();

                try
                {
                    await store.DropAsync(ids, off, now);
                    dropped.UnionWith(ids);
                }
                catch (Exception e)
                {
                    Console.WriteLine(PostingTexts.DropFailed(group.Key.source, group.Key.guild_id, FailureLog.Describe(e)));
                    break;
                }
            }

            return dropped;
        }

        // What waits for the resume: what waited before this run and wasn't dropped, and what it queued and reopened
        static string Paused(IEnumerable<QueueRow> open, PostingState state, IReadOnlyList<QueueRow> hidden, Detection detection)
        {
            var reopened = detection.ReopenArmor.ToHashSet();
            var shown = detection.ReopenMaps.ToHashSet();

            var waiting = open.Select(row => (row.guild_id, row.source, row.item_id))
                .Concat(detection.Queue.Select(item => (item.guild_id, item.source, item.item_id)))
                .Concat(state.Withdrawn.Where(row => reopened.Contains(row.item_id)).Select(row => (row.guild_id, row.source, row.item_id)))
                .Concat(hidden.Where(row => shown.Contains(row.id)).Select(row => (row.guild_id, row.source, row.item_id)))
                .Distinct()
                .ToList();

            int Items(string source) => waiting.Where(item => item.source == source).Select(item => item.item_id).Distinct().Count();

            return PostingTexts.Paused(Items(PostSource.Armor), Items(PostSource.Maps), Items(PostSource.Lobbies), waiting.Select(item => item.guild_id).Distinct().Count());
        }

        // A call to MinerBot's database that the run can't go on without: posts wait for the next run, and the run fails
        // saying why, with the exception's type and MySQL's number, never its message
        static async Task<T> DatabaseAsync<T>(Func<Task<T>> call)
        {
            try
            {
                return await call();
            }
            catch (Exception e)
            {
                throw new JobFailedException(PostingTexts.DatabaseDidntAnswer(FailureLog.Describe(e)));
            }
        }
    }
}
