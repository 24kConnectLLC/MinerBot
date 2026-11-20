using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Status;
using System.Diagnostics;

namespace MinerBot_2._0.Services
{
    // Sends a direct message about problems only the bot's owner can fix, to the user in ALERT_USER_ID or else the owner of the Discord application
    public class OwnerAlertService
    {
        public const string RecipientSetting = "ALERT_USER_ID";

        private static readonly TimeSpan ReadyLimit = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ReadyCheckInterval = TimeSpan.FromMilliseconds(100);

        // How much of an alert that couldn't be sent goes into the bot's history with the reason
        const int StartOfMessage = 200;

        private readonly DiscordSocketClient _client;
        private readonly IConfiguration _configuration;
        private readonly BotRecorder _recorder;

        public OwnerAlertService(DiscordSocketClient client, IConfiguration configuration, BotRecorder recorder)
        {
            _client = client;
            _configuration = configuration;
            _recorder = recorder;
        }

        // Each alert goes into the bot's history too. Its text is the bot's own wording, which never holds a secret. Steam's
        // AlertOwner is this method, so it keeps one parameter: an optional second one wouldn't convert to Func<string, Task>.
        public Task SendAsync(string message) => SendAsync(message, message);

        // For a message that holds someone's name or words, such as one about a dashboard request: the bot's history keeps
        // recordAs in its place, because Admins read it, and an event holds no name and no one's text
        public async Task SendAsync(string message, string recordAs)
        {
            try
            {
                await WaitForDiscord();

                var recipient = await _client.GetUserAsync(await GetRecipientId());
                var channel = await recipient.CreateDMChannelAsync();

                await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
            }
            catch (Exception e)
            {
                // The caller reports it as well
                _recorder.Record(BotEvents.AlertFailed, $"{e.GetType().Name}: {Start(recordAs)}");
                throw;
            }

            _recorder.Record(BotEvents.AlertSent, recordAs);
        }

        // A character outside the Basic Multilingual Plane is kept whole or left out
        static string Start(string message)
        {
            if (message.Length <= StartOfMessage)
                return message;

            var end = char.IsHighSurrogate(message[StartOfMessage - 1]) ? StartOfMessage - 1 : StartOfMessage;
            return message[..end];
        }

        // The application's owner is the default, but for a team that's the team's owner, who may not be the one running the bot
        private async Task<ulong> GetRecipientId()
        {
            var setting = _configuration[RecipientSetting];

            if (ulong.TryParse(setting, out var userId) && userId != 0)
                return userId;

            // A value that isn't an ID still lets the alert reach someone
            if (!string.IsNullOrWhiteSpace(setting))
                Console.WriteLine($"{RecipientSetting} isn't a Discord user ID, so the alert goes to the owner of the Discord application");

            var application = await _client.GetApplicationInfoAsync();

            // The owner of an application that belongs to a team is only a placeholder for the team
            return application.Team?.OwnerUserId ?? application.Owner.Id;
        }

        // Opening a direct message fails until the gateway is ready, and Steam can raise an alert before that
        private async Task WaitForDiscord()
        {
            var clock = Stopwatch.StartNew();

            while (_client.CurrentUser == null && clock.Elapsed < ReadyLimit)
                await Task.Delay(ReadyCheckInterval);
        }
    }
}
