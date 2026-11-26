using DataAccessLibrary.Status.Models;

namespace MinerBot.Dashboard.Status
{
    // Which bot runs, as far as its posts go. A bot from PR 11 on keeps what it finds and what each server is owed in
    // MinerBot's database, reads the switches at every run and records every post, and writes its own run's ID as
    // posting_run_id in every write of its status row. An older one, as ./rollback.sh bot puts back, doesn't name the column,
    // so it keeps the value the newer bot left, which differs from the run's ID within 30 seconds of its start. Without it, the
    // armor review page keeps the words and rules of a bot that holds its posts only in memory, which a restart loses, and the
    // Overview says the bot is older than the dashboard. The other pages' words, the pause's and restart's help and the
    // paused banner among them, describe the current bot, and the Overview's banner tells the exception.
    internal static class PostingRules
    {
        public static bool KeepsPosts(BotStatusRow? status) => status is { posting_run_id: { } run } && run == status.run_id;
    }
}
