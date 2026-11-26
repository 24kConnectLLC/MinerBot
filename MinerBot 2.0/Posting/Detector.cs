using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Steam.Models;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Posting
{
    // What one run read: the approved armor, Steam's latest maps and the open lobbies, each null when its read failed
    public sealed record PostingReads(IReadOnlyList<ArmorStyle>? Armor, IReadOnlyList<PublishedFileDetailsModel>? Maps, IReadOnlyList<LobbyInfo>? Lobbies);

    // A baseline a source took this run: its first, or a new one because an older bot posted since the time given
    public sealed record TakenBaseline(string Source, string Reason, int Items, DateTime? OlderBotSince = null);

    // Everything the detector decides from: the reads, what MinerBot's database holds of them, and the servers owed each kind
    // now. Times are UTC, the bot's own, but for the maps' updates and the maps baseline's time, which are Steam's.
    public sealed class DetectorInput
    {
        public DateTime Now { get; init; }
        public ulong RunId { get; init; }
        public PostingReads Reads { get; init; } = new(null, null, null);
        public IReadOnlyList<BaselineRow> Baselines { get; init; } = [];

        // The seen rows of the reads' items
        public IReadOnlyList<SeenRow> Seen { get; init; } = [];

        // The rows of the approved armor that were dropped because it wasn't approved any more
        public IReadOnlyList<QueueRow> Withdrawn { get; init; } = [];

        // The rows of the read's maps that were dropped because Steam didn't show the map any more
        public IReadOnlyList<QueueRow> Hidden { get; init; } = [];

        // The servers each kind the bot posts is owed to now, by source, in the order to queue them
        public IReadOnlyDictionary<string, IReadOnlyList<ulong>> Owed { get; init; } = new Dictionary<string, IReadOnlyList<ulong>>();

        // The sources that take a new baseline, because an older bot posted since their last check, with that check's time
        public IReadOnlyDictionary<string, DateTime> OlderBot { get; init; } = new Dictionary<string, DateTime>();
    }

    // What a run found, to save in one transaction, and the baselines it took
    public sealed record DetectorResult(Detection Detection, IReadOnlyList<TakenBaseline> Baselines);

    // Finds what's new in a run's reads and queues it for the servers owed it now. Pure: no I/O and no clock, so every rule is
    // tested on its own (MapDetectorTests). A source whose read failed is left alone, so its next good read finds what's new.
    //
    // A source with no baseline takes its first good read as one: everything in it is taken as already posted, so a first
    // start floods nothing. So does a source an older bot posted for since this bot last checked, as after a rollback and a
    // forward deploy: the older bot posted what was new to it, which this bot can't tell apart, and the queue stays, since
    // the older bot's own first read took what this bot had queued as its baseline and never posted it.
    //
    // Armor: an approved ID never seen is queued; one approved again reopens only its rows dropped as not approved, so a
    // server that got it never gets it again, and a baseline armor is never queued.
    // Maps: by Steam's update times. A map never seen is new when it was made after the baseline's time, an update when it
    // was updated after it, and otherwise it moved up into the read from below, which isn't news. A seen map updated since
    // is posted again, unless it was queued within the cooldown: then it's passed over, unrecorded, so the first read after
    // the cooldown posts it, but not where a post of the map showed it meanwhile, such as one that waited or went again:
    // each post records the update it shows for its server (MessagePlan). In a read after a gap, a map updated long before
    // the newest of the read is old news; a read minutes after the last posts what's new to it however Steam dates it. A map
    // shown again after its posts were dropped as hidden, with the update they were for or a newer one made meanwhile,
    // reopens those rows, so it goes where it never went, at once and as it is then.
    // A map without an update time says nothing about when it changed, so it's left out of the read.
    // Lobbies: an open lobby never seen is queued, or ignored when its version's lobbies aren't posted, and an open lobby
    // seen before has when it was last seen written again at most once an hour.
    public static class Detector
    {
        public static DetectorResult Detect(DetectorInput input)
        {
            var run = new Run(input);

            if (input.Reads.Armor is { } armor)
                run.Armor(armor);

            if (input.Reads.Maps is { } maps)
                run.Maps(maps);

            if (input.Reads.Lobbies is { } lobbies)
                run.Lobbies(lobbies);

            return run.Result();
        }

        // The maps of a read the rules go by, in Steam's order: each once, and only with an update time
        public static List<PublishedFileDetailsModel> Dated(IEnumerable<PublishedFileDetailsModel> maps) =>
            maps.Where(map => map.PublishedFileId != 0 && map.TimeUpdated != default).DistinctBy(map => map.PublishedFileId).ToList();

        // Steam's reader gives UTC without saying so
        public static DateTime Utc(DateTime steamTime) => DateTime.SpecifyKind(steamTime, DateTimeKind.Utc);

        // A map's update as the queue keeps it: Steam's Unix time
        public static ulong Revision(PublishedFileDetailsModel map) => Revision(map.TimeUpdated);

        public static ulong Revision(DateTime steamTime) => (ulong)Math.Max(0, new DateTimeOffset(Utc(steamTime)).ToUnixTimeSeconds());

        sealed class Run(DetectorInput input)
        {
            readonly DateTime now = input.Now;
            readonly Dictionary<string, BaselineRow> baselines = input.Baselines.ToDictionary(row => row.source, StringComparer.Ordinal);
            readonly Dictionary<(string, ulong), SeenRow> seen = input.Seen.ToDictionary(row => (row.source, row.item_id));

            readonly List<BaselineRow> baselineRows = new();
            readonly List<SeenRow> seenRows = new();
            readonly List<QueueItem> queue = new();
            readonly List<ulong> reopen = new();
            readonly List<ulong> reopenMaps = new();
            readonly List<TakenBaseline> taken = new();

            public DetectorResult Result() =>
                new(new Detection { At = now, Baselines = baselineRows, Seen = seenRows, Queue = queue, ReopenArmor = reopen, ReopenMaps = reopenMaps }, taken);

            IReadOnlyList<ulong> Owed(string source) => input.Owed.TryGetValue(source, out var guilds) ? guilds : [];

            SeenRow? Seen(string source, ulong id) => seen.GetValueOrDefault((source, id));

            // A baseline to take now, with its reason, or null when the source keeps the one it has
            string? BaselineReasonOf(string source) =>
                !baselines.ContainsKey(source) ? BaselineReason.First : input.OlderBot.ContainsKey(source) ? BaselineReason.OlderBot : null;

            void TakeBaseline(string source, string reason, int items, DateTime? baselineTime = null)
            {
                baselineRows.Add(new BaselineRow
                {
                    source = source,
                    taken_at = now,
                    taken_by_run = input.RunId,
                    reason = reason,
                    item_count = (uint)items,
                    baseline_time = baselineTime,
                    checked_at = now,
                    checked_by_run = input.RunId
                });

                taken.Add(new TakenBaseline(source, reason, items, reason == BaselineReason.OlderBot ? input.OlderBot[source] : null));
            }

            // The source's baseline as it was, read again by this run
            void Checked(string source) => baselineRows.Add(baselines[source] with { checked_at = now, checked_by_run = input.RunId });

            SeenRow New(string source, ulong id, string state) => new()
            {
                source = source,
                item_id = id,
                state = state,
                first_seen_at = now,
                last_seen_at = now
            };

            void Queue(string source, ulong id, ulong revision = 0, bool isNew = true)
            {
                foreach (var guildId in Owed(source))
                    queue.Add(new QueueItem { guild_id = guildId, source = source, item_id = id, revision = revision, is_new = isNew, queued_at = now });
            }

            public void Armor(IReadOnlyList<ArmorStyle> read)
            {
                var ids = read.Where(armor => armor.ID > 0).Select(armor => (ulong)armor.ID).Distinct().ToList();
                var withdrawn = input.Withdrawn.Where(row => row.source == PostSource.Armor).Select(row => row.item_id).ToHashSet();

                if (BaselineReasonOf(PostSource.Armor) is { } reason)
                {
                    foreach (var id in ids)
                    {
                        // At a new baseline, an armor approved again may be what the older bot posted, so it's never reopened
                        if (Seen(PostSource.Armor, id) is not { } row)
                            seenRows.Add(New(PostSource.Armor, id, SeenState.Baseline));
                        else if (withdrawn.Contains(id) && row.state == SeenState.Queued)
                            seenRows.Add(row with { state = SeenState.Baseline, last_seen_at = now });
                    }

                    TakeBaseline(PostSource.Armor, reason, ids.Count);
                    return;
                }

                foreach (var id in ids)
                {
                    if (Seen(PostSource.Armor, id) is not { } row)
                    {
                        seenRows.Add(New(PostSource.Armor, id, SeenState.Queued));
                        Queue(PostSource.Armor, id);
                    }
                    else if (withdrawn.Contains(id) && row.state == SeenState.Queued)
                        reopen.Add(id);
                }

                Checked(PostSource.Armor);
            }

            public void Maps(IReadOnlyList<PublishedFileDetailsModel> read)
            {
                var maps = Dated(read);
                var hidden = input.Hidden.Where(row => row.source == PostSource.Maps).ToList();

                if (BaselineReasonOf(PostSource.Maps) is { } reason)
                {
                    // Nothing to set it with, so the next read tries again
                    if (maps.Count == 0)
                        return;

                    foreach (var map in maps)
                    {
                        var updated = Utc(map.TimeUpdated);

                        // At a new baseline, a map shown again may be what the older bot posted, so its rows dropped as hidden
                        // are never reopened
                        if (Seen(PostSource.Maps, map.PublishedFileId) is not { } row)
                            seenRows.Add(New(PostSource.Maps, map.PublishedFileId, SeenState.Baseline) with { updated_at = updated });
                        else if (row.updated_at is not { } recorded || updated > recorded)
                            seenRows.Add(row with { state = SeenState.Baseline, updated_at = updated, last_seen_at = now });
                        else if (row.state == SeenState.Queued && hidden.Exists(dropped => dropped.item_id == map.PublishedFileId))
                            seenRows.Add(row with { state = SeenState.Baseline, last_seen_at = now });
                    }

                    TakeBaseline(PostSource.Maps, reason, maps.Count, maps.Min(map => Utc(map.TimeUpdated)));
                    return;
                }

                var baselineTime = baselines[PostSource.Maps].baseline_time;
                var newest = maps.Count == 0 ? default : maps.Max(map => Utc(map.TimeUpdated));
                var owed = Owed(PostSource.Maps);

                // A read after a gap, as deep as a baseline's, is the only one with old news: it bounds what a catch-up can post.
                // A read minutes after the last posts what's new to it however long ago Steam dates it, as a map made public,
                // or shown again, a day after its last update.
                var catchUp = PostingLimits.MapReadDepth(now, baselines[PostSource.Maps].checked_at, baseline: false) == PostingLimits.DeepMaps;

                foreach (var map in maps)
                {
                    var updated = Utc(map.TimeUpdated);
                    var row = Seen(PostSource.Maps, map.PublishedFileId);
                    bool isNew;

                    if (row != null)
                    {
                        if (row.updated_at is { } recorded && updated <= recorded)
                        {
                            // Shown again with the update it was queued for, after Steam stopped showing it while its posts
                            // waited: the ones dropped as hidden go out after all, where it never went
                            if (row.state == SeenState.Queued)
                                reopenMaps.AddRange(hidden.Where(dropped => dropped.item_id == map.PublishedFileId && dropped.revision == Revision(map)).Select(dropped => dropped.id));

                            continue;
                        }

                        // Shown again with a newer update than it was queued for, such as its author's fix made while Steam
                        // didn't show it: the rows of that queueing dropped as hidden go out too, now, in the cooldown as after
                        // it, rather than as an update a day later, or never once other maps push it out of the read. So do the
                        // rows of newer updates dropped with them, which a post that wasn't made showed: the post holds them, so
                        // none is reopened again once its update is queued. The post shows the map as it is, which records the
                        // newer update for its server.
                        if (row.state == SeenState.Queued && row.updated_at is { } queuedFor)
                            reopenMaps.AddRange(hidden.Where(dropped => dropped.item_id == map.PublishedFileId && dropped.revision >= Revision(queuedFor)).Select(dropped => dropped.id));

                        // Not recorded either, so the first read after the cooldown posts the update. A post of the map that goes
                        // meanwhile shows it and records it for its server, where that read's queueing then adds nothing.
                        if (row.queued_at is { } queuedAt && now - queuedAt < PostingLimits.Cooldown)
                            continue;

                        isNew = false;
                    }
                    else if (Utc(map.TimeCreated) > baselineTime)
                        isNew = true;
                    else if (updated > baselineTime)
                        isNew = false;
                    else
                    {
                        // It moved up into the read when a map above it was hidden or removed
                        seenRows.Add(New(PostSource.Maps, map.PublishedFileId, SeenState.Baseline) with { updated_at = updated });
                        continue;
                    }

                    if (catchUp && newest - updated > PostingLimits.OldNews)
                    {
                        seenRows.Add((row ?? New(PostSource.Maps, map.PublishedFileId, SeenState.Baseline)) with
                        {
                            state = SeenState.Baseline,
                            updated_at = updated,
                            last_seen_at = now
                        });

                        continue;
                    }

                    // A map that no server is owed isn't posted, so it starts no cooldown
                    seenRows.Add((row ?? New(PostSource.Maps, map.PublishedFileId, SeenState.Queued)) with
                    {
                        state = SeenState.Queued,
                        updated_at = updated,
                        queued_at = owed.Count > 0 ? now : row?.queued_at,
                        last_seen_at = now
                    });

                    Queue(PostSource.Maps, map.PublishedFileId, Revision(map), isNew);
                }

                Checked(PostSource.Maps);
            }

            public void Lobbies(IReadOnlyList<LobbyInfo> read)
            {
                var lobbies = read.Where(lobby => lobby.LobbyId != 0).DistinctBy(lobby => lobby.LobbyId).ToList();
                var reason = BaselineReasonOf(PostSource.Lobbies);

                foreach (var lobby in lobbies)
                {
                    if (Seen(PostSource.Lobbies, lobby.LobbyId) is { } row)
                    {
                        if (now - row.last_seen_at >= PostingLimits.LobbySeenAgain)
                            seenRows.Add(row with { last_seen_at = now });
                    }
                    else if (reason != null)
                        seenRows.Add(New(PostSource.Lobbies, lobby.LobbyId, SeenState.Baseline));
                    else if (!GameVersions.Notifies(lobby.VersionNumber))
                        seenRows.Add(New(PostSource.Lobbies, lobby.LobbyId, SeenState.Ignored));
                    else
                    {
                        seenRows.Add(New(PostSource.Lobbies, lobby.LobbyId, SeenState.Queued));
                        Queue(PostSource.Lobbies, lobby.LobbyId);
                    }
                }

                if (reason != null)
                    TakeBaseline(PostSource.Lobbies, reason, lobbies.Count);
                else
                    Checked(PostSource.Lobbies);
            }
        }
    }
}
