using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using Microsoft.AspNetCore.WebUtilities;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Status;
using MinerBot.Shared.Requests;

namespace MinerBot.Dashboard.Requests
{
    // A request as its box shows it, with the row it was built from
    public sealed record RequestBox(RequestPageRow Row, RequestView View);

    // A request's box as the pages draw it: the request's own page and its fragment, and a page whose form's redirect names
    // the request it sent in ?request=. The read gives every time through the guard, so a row MySql.Data can't read never
    // takes a page down.
    internal sealed class RequestBoxes(IBotRequestReads requests, IBotStatusReads status, TimeProvider time, ILogger<RequestBoxes> logger)
    {
        // Check again's own parameter, which no page reads
        public const string Again = "again";

        // The request and its box, or null for an ID that names none. The bot's status is read only where the box needs it:
        // for a request that waits, whether the bot is on Discord, and for a restart that's done, whether it's back. Then,
        // only while that row is a new run's, up on Discord and not stopped, the bot's own record of when that run first
        // connected, which is exact: the row's discord_since is the bot's next 5-second sample after it.
        public async Task<RequestBox?> LoadAsync(ulong id)
        {
            var row = await DatabaseCalls.Guard(() => requests.GetRequestView(id), logger);

            if (row == null)
                return null;

            var restart = row.kind == RequestKinds.BotRestart && row.state == RequestStates.Done;
            var bot = row.state == null || restart ? await DatabaseCalls.Guard(() => status.GetStatus(), logger) : null;

            // The first connection at or after the new run's start, never an earlier run's, nor a later reconnect: a row at
            // that time with id 0 stands for every event from that instant on. The bot writes it just after the status row
            // that first says connected, so it can be missing for a moment, or for good if the bot dropped it.
            var connected = restart && bot is { stopped_at: null, discord_state: DiscordState.Connected } && bot.run_id != row.run_id
                ? await DatabaseCalls.Guard(() => status.FirstEventAfter(EventWords.DiscordConnected, new BotEventRow { at = bot.started_at }), logger)
                : null;

            return new RequestBox(row, RequestViews.Of(row, bot, time.GetUtcNow().UtcDateTime, RequestViews.Waits, connected));
        }

        // Check again, the next look at a box without the script: the page's own address with the box's parameters, which
        // the page builds from what it knows, never from the query it was sent, with again=1 added, or taken off when the
        // page was opened with again, in any letter case and with any value or none. So the link never equals the address
        // the browser shows, to which a link with a fragment only scrolls: each press loads the page, at #result, and the
        // next flips it back.
        public static string CheckAgain(string address, IQueryCollection shown) =>
            (shown.ContainsKey(Again) ? address : QueryHelpers.AddQueryString(address, Again, "1")) + "#result";

        // The box for a page's ?request=, only when it names a request the page shows: one of the page's kinds and, on a
        // server's page, that server's. Anything else shows nothing, as ?done= does: the parameter only chooses what to show,
        // and no check depends on it. checkAgain is the link CheckAgain built, the next look without the script.
        public async Task<RequestSection?> SectionAsync(ulong? id, IReadOnlyCollection<string> kinds, ulong? guildId, string checkAgain)
        {
            if (id is not { } named || named == 0 || await LoadAsync(named) is not { } box || !Fits(box.Row, kinds, guildId))
                return null;

            return new RequestSection(box.View, checkAgain);
        }

        public static bool Fits(RequestPageRow row, IReadOnlyCollection<string> kinds, ulong? guildId) =>
            kinds.Contains(row.kind) && (guildId == null || row.guild_id == guildId);
    }
}
