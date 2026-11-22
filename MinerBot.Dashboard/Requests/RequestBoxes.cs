using DataAccessLibrary.Requests;
using DataAccessLibrary.Requests.Models;
using DataAccessLibrary.Status;
using MinerBot.Dashboard.Security;
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
        // The request and its box, or null for an ID that names none. The bot's status is read only where the box needs it:
        // for a request that waits, whether the bot is on Discord, and for a restart that's done, whether it's back.
        public async Task<RequestBox?> LoadAsync(ulong id)
        {
            var row = await DatabaseCalls.Guard(() => requests.GetRequestView(id), logger);

            if (row == null)
                return null;

            var bot = row.state == null || (row.kind == RequestKinds.BotRestart && row.state == RequestStates.Done)
                ? await DatabaseCalls.Guard(() => status.GetStatus(), logger)
                : null;

            return new RequestBox(row, RequestViews.Of(row, bot, time.GetUtcNow().UtcDateTime, RequestViews.Waits));
        }

        // The box for a page's ?request=, only when it names a request the page shows: one of the page's kinds and, on a
        // server's page, that server's. Anything else shows nothing, as ?done= does: the parameter only chooses what to show,
        // and no check depends on it. checkAgain is the page's own address with the parameter, the next look without the
        // script.
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
