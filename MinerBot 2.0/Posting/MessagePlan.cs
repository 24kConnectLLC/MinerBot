using DataAccessLibrary.Posting;
using DataAccessLibrary.Posting.Models;
using Discord;
using MinerBot.Shared.Delivery;
using MinerBot_2._0.Services;
using Steam.Models;
using SteamWebAPIAccess;

namespace MinerBot_2._0.Posting
{
    // One message of a post, with exactly the queue rows of the items it carries, and for maps the updates it shows that are
    // newer than those rows are for
    public sealed record PlannedMessage(string? Text, Embed[] Embeds, IReadOnlyList<ulong> QueueIds)
    {
        public IReadOnlyList<ShownUpdate> Shows { get; init; } = [];
    }

    // A server's kind's post from its waiting rows: its messages, and the rows dropped because their item is gone. A row in
    // neither waits, because the read it needs failed.
    public sealed record GroupPlan(IReadOnlyList<PlannedMessage> Messages, IReadOnlyList<(ulong QueueId, string Reason)> Drops);

    // Builds the messages of each server's kind from what waits for it, by the same builders as before, with this run's reads:
    // - armor: the rows whose armor is still approved, by ArmorService.NewArmorListEmbeds, a message mapped to the armor it
    //   lists; the rest are dropped as not approved, but only after a good read
    // - maps: one embed for each map, however many of its updates wait, posted as an update unless one of them was new, by
    //   SteamAPIService.GetLatestMapsMessages; the details from this run's read, or else one look-up of every map that waits
    //   and isn't in it. A map Steam doesn't show any more is dropped as hidden; Steam not answering leaves them waiting. The
    //   post shows each map as Steam has it now, which can be an update newer than its rows are for, made while they waited
    //   or found by no read yet: the message says so, and its claim records that update for the server, so the update isn't
    //   posted there again once its cooldown is over.
    // - lobbies: one message of the lobbies still open, every row in it counting, those past the embed's room too; the rest
    //   are dropped as closed, but only after a good read
    // One for each run, so the maps' details are looked up once.
    public sealed class MessagePlan(SteamAPIService steamService, IGetData steam, PostingReads reads, IReadOnlyList<QueueRow> open)
    {
        // The type in front of the lobbies' title, which says the post is of new lobbies, and the title, by which the
        // notification run knows its own post in a channel
        const string NewLobbies = "__NEW__";
        public static readonly string LobbiesTitle = SteamAPIService.LobbiesTitle(NewLobbies);

        // The details of the maps that wait and aren't in this run's read, once they're looked up: null when Steam didn't answer
        Dictionary<ulong, PublishedFileDetailsModel>? lookedUp;
        bool lookedUpAlready;

        public Task<GroupPlan> PlanAsync(string source, IReadOnlyList<QueueRow> rows) => source switch
        {
            PostSource.Armor => Task.FromResult(Armor(rows)),
            PostSource.Maps => MapsAsync(rows),
            PostSource.Lobbies => LobbiesAsync(rows),
            _ => throw new ArgumentException($"Only these kinds are posted: {string.Join(", ", PostSource.Posted)}", nameof(source))
        };

        GroupPlan Armor(IReadOnlyList<QueueRow> rows)
        {
            if (reads.Armor is not { } approved)
                return new GroupPlan([], []);

            // In the read's order, which is by name, as before
            var place = approved.Select((armor, index) => (armor, index)).Where(item => item.armor.ID > 0).DistinctBy(item => item.armor.ID)
                .ToDictionary(item => (ulong)item.armor.ID);

            var carried = rows.Where(row => place.ContainsKey(row.item_id)).OrderBy(row => place[row.item_id].index).ToList();
            var drops = rows.Where(row => !place.ContainsKey(row.item_id)).Select(row => (row.id, DeliveryReasons.NotApproved)).ToList();

            var messages = new List<PlannedMessage>();
            var taken = 0;

            foreach (var (embed, armorCount) in ArmorService.NewArmorListEmbeds(carried.Select(row => place[row.item_id].armor).ToList()))
            {
                messages.Add(new PlannedMessage(null, [embed], carried.Skip(taken).Take(armorCount).Select(row => row.id).ToList()));
                taken += armorCount;
            }

            return new GroupPlan(messages, drops);
        }

        async Task<GroupPlan> MapsAsync(IReadOnlyList<QueueRow> rows)
        {
            var read = Detector.Dated(reads.Maps ?? []).Select((map, index) => (map, index)).ToDictionary(item => item.map.PublishedFileId);
            var details = await LookUpAsync(read.Keys);
            var maps = new List<(PublishedFileDetailsModel Map, bool IsNew, int Place, List<ulong> Rows, ulong Newest)>();
            var drops = new List<(ulong, string)>();

            foreach (var same in rows.GroupBy(row => row.item_id))
            {
                var ids = same.Select(row => row.id).ToList();
                var isNew = same.Any(row => row.is_new);
                var newest = same.Max(row => row.revision);

                if (read.TryGetValue(same.Key, out var inRead))
                    maps.Add((inRead.map, isNew, inRead.index, ids, newest));
                else if (details == null)
                    continue;
                else if (details.TryGetValue(same.Key, out var map))
                    maps.Add((map, isNew, int.MaxValue, ids, newest));
                else
                    drops.AddRange(ids.Select(id => (id, DeliveryReasons.Hidden)));
            }

            // In Steam's order, newest update first, the maps out of the read after the ones in it
            maps = maps.OrderBy(map => map.Place).ThenByDescending(map => map.Map.TimeUpdated).ToList();

            if (maps.Count == 0)
                return new GroupPlan([], drops);

            var messages = new List<PlannedMessage>();
            var taken = 0;

            foreach (var (text, embeds) in await steamService.GetLatestMapsMessages(maps.Select(map => (map.Map, map.IsNew)).ToList()))
            {
                var carried = maps.Skip(taken).Take(embeds.Length).ToList();

                messages.Add(new PlannedMessage(text, embeds, carried.SelectMany(map => map.Rows).ToList())
                {
                    Shows = carried.Where(map => Detector.Revision(map.Map) > map.Newest).Select(map => new ShownUpdate(map.Map.PublishedFileId, Detector.Revision(map.Map))).ToList()
                });

                taken += embeds.Length;
            }

            return new GroupPlan(messages, drops);
        }

        // Every map that waits and isn't in this run's read, in one call for the whole run, made when a post first needs it
        async Task<Dictionary<ulong, PublishedFileDetailsModel>?> LookUpAsync(IEnumerable<ulong> inRead)
        {
            if (lookedUpAlready)
                return lookedUp;

            var known = inRead.ToHashSet();
            var missing = open.Where(row => row.source == PostSource.Maps && !known.Contains(row.item_id)).Select(row => row.item_id).Distinct().ToList();

            lookedUpAlready = true;

            if (missing.Count == 0)
                return lookedUp = new();

            try
            {
                lookedUp = (await steam.GetPublicWorkshopItems(missing)).DistinctBy(map => map.PublishedFileId).ToDictionary(map => map.PublishedFileId);
            }
            catch (Exception e)
            {
                Console.WriteLine(PostingTexts.MapsNotLookedUp(e.GetType().Name));
                lookedUp = null;
            }

            return lookedUp;
        }

        async Task<GroupPlan> LobbiesAsync(IReadOnlyList<QueueRow> rows)
        {
            if (reads.Lobbies is not { } openLobbies)
                return new GroupPlan([], []);

            var place = openLobbies.Where(lobby => lobby.LobbyId != 0).Select((lobby, index) => (lobby, index)).DistinctBy(item => item.lobby.LobbyId)
                .ToDictionary(item => item.lobby.LobbyId);

            var carried = rows.Where(row => place.ContainsKey(row.item_id)).OrderBy(row => place[row.item_id].index).ToList();
            var drops = rows.Where(row => !place.ContainsKey(row.item_id)).Select(row => (row.id, DeliveryReasons.Closed)).ToList();

            if (carried.Count == 0)
                return new GroupPlan([], drops);

            var embed = await steamService.LobbyListEmbed(await steamService.GetLobbiesDisplay(carried.Select(row => place[row.item_id].lobby).ToList(), hideEmpty: true),
                type: NewLobbies);

            return new GroupPlan([new PlannedMessage(null, [embed], carried.Select(row => row.id).ToList())], drops);
        }
    }
}
