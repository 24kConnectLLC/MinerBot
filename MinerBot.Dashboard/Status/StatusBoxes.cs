using DataAccessLibrary;
using DataAccessLibrary.Health;
using DataAccessLibrary.Status;
using DataAccessLibrary.Status.Models;
using MinerBot.Dashboard.Health;
using MinerBot.Dashboard.Security;

namespace MinerBot.Dashboard.Status
{
    // What the status box shows: the bot's status row, null when there's none, its state by the page's clock, whether the
    // website's database answers the dashboard, and the time it was drawn
    public sealed record StatusBox(BotStatusRow? Status, BotState State, bool WebsiteAnswers, DateTime Now);

    // The status box the Overview and the Bot status page show, and refresh.js gets again every 30 seconds from the
    // Overview's Status handler, which marks it as the status fragment
    internal sealed class StatusBoxes(IBotStatusReads reads, IDatabaseHealthData databases, TimeProvider time, ILogger<StatusBoxes> logger)
    {
        public const string Fragment = "status";
        public const string Address = "/admin?handler=status";

        // MinerBot's database first, which sessions need anyway, so its failure is the usual page. The website's database
        // is asked as the readiness check asks it, which never throws, unless the page has asked it already.
        public async Task<StatusBox> LoadAsync(bool? websiteAnswers = null)
        {
            var status = await DatabaseCalls.Guard(() => reads.GetStatus(), logger);
            var answers = websiteAnswers ?? await databases.Answers(DBConnectionName.Armor, DatabaseHealthCheck.Limit);
            var now = time.GetUtcNow().UtcDateTime;

            return new StatusBox(status, BotStates.Of(status, now), answers, now);
        }
    }

    internal static class StatusPages
    {
        public static IServiceCollection AddStatusPages(this IServiceCollection services)
        {
            services.AddTransient<StatusBoxes>();
            return services;
        }
    }
}
