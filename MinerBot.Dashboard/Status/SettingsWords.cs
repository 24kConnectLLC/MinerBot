using System.Globalization;
using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Status
{
    // What Discord shows as MinerBot's status: its default, a stored text the rules pass, or the default because the stored
    // text breaks them
    public enum StatusShown
    {
        Default,
        Text,
        Refused
    }

    // The status line in parts, so a page draws a stored text through RevealedText between fixed words. Text is set only for
    // a text the rules pass, and Rule only for one they refuse, which is never shown.
    public sealed record StatusLine(StatusShown Shown, string Start, string? Text, string End, string? Rule);

    // The bot's settings in words, from bot_settings, which only the bot writes, after a signed request. No row means posts
    // aren't paused and the status is the default. Every page that shows the status goes through StatusLine, which follows
    // the bot's own rule: a stored text the shared rules refuse, after a rules change or written by hand, isn't shown, and
    // Discord shows the default. A reason is shown with each hidden character as its code: the bot never declines one.
    internal static class SettingsWords
    {
        public const string DefaultStatus = "Playing Murder Miners on my PC";

        // Paused, since when, on which request and by whom, with the reason given; or not paused, and when posts were last
        // resumed. The name is the actor of the request that paused them, as its row has it, and none once that row is gone.
        public static string PauseLine(BotSettingsRow? settings, string? pausedBy)
        {
            if (settings is not { posts_paused: true })
            {
                return settings is { pause_changed_at: { } resumed }
                    ? $"Posts aren't paused. They were last resumed at {TimeWords.Utc(resumed)}{OnRequest(settings.pause_request_id)}."
                    : "Posts aren't paused.";
            }

            var since = settings.pause_changed_at is { } at ? $", since {TimeWords.Utc(at)}" : "";
            var by = pausedBy is { } name ? $", by {TextInspector.Reveal(name)}" : "";
            var reason = settings.pause_reason is { } why ? $" The reason given: {TextInspector.Reveal(why)}." : "";

            return $"Posts are paused{since}{OnRequest(settings.pause_request_id)}{by}.{reason}";
        }

        public static StatusLine StatusLine(BotSettingsRow? settings)
        {
            if (settings?.status_text is not { } text)
                return new StatusLine(StatusShown.Default, $"Discord shows the default status, {DefaultStatus}.", null, "", null);

            if (RequestRules.StatusTextProblem(text) is { } rule)
                return new StatusLine(StatusShown.Refused, $"Discord shows the default status, {DefaultStatus}, because the stored status text breaks the rules ({rule}).",
                    null, "", rule);

            var set = settings.status_changed_at is { } at ? $", set at {TimeWords.Utc(at)}" : "";
            return new StatusLine(StatusShown.Text, "Discord shows the status text ", text, $"{set}{OnRequest(settings.status_request_id)}.", null);
        }

        static string OnRequest(ulong? id) => id is { } request ? ", on request " + request.ToString(CultureInfo.InvariantCulture) : "";
    }
}
