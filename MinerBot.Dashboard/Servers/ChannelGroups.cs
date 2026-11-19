using DataAccessLibrary.Guilds.Models;

namespace MinerBot.Dashboard.Servers
{
    // One category of a server's channels, or the channels outside any, which come first: the channels MinerBot can see,
    // in Discord's order, and how many it can't
    public sealed record ChannelGroup(ulong? CategoryId, string? Category, IReadOnlyList<BotChannelRow> Channels, int Hidden);

    internal static class ChannelGroups
    {
        // Discord's name for a channel it hides from the bot, which the bot stores as it comes, with can_view 0
        public const string HiddenName = "___hidden___";

        public static bool IsHidden(BotChannelRow channel) => channel.name == HiddenName && !channel.can_view;

        // A category Discord hides from the bot comes with the same name
        public static bool IsHiddenCategory(string? category) => category == HiddenName;

        // The channels come in Discord's order (BotGuildReads.ListChannels), where each category's channels are together, so
        // a group starts wherever the category changes. Two categories of the same name stay apart, by their IDs.
        public static IReadOnlyList<ChannelGroup> Of(IReadOnlyList<BotChannelRow> channels)
        {
            var groups = new List<ChannelGroup>();

            foreach (var run in Runs(channels))
            {
                var first = run[0];
                groups.Add(new ChannelGroup(first.category_id, first.category, run.Where(channel => !IsHidden(channel)).ToList(), run.Count(IsHidden)));
            }

            return groups;
        }

        static IEnumerable<List<BotChannelRow>> Runs(IReadOnlyList<BotChannelRow> channels)
        {
            var run = new List<BotChannelRow>();

            foreach (var channel in channels)
            {
                if (run.Count > 0 && run[0].category_id != channel.category_id)
                {
                    yield return run;
                    run = [];
                }

                run.Add(channel);
            }

            if (run.Count > 0)
                yield return run;
        }
    }
}
