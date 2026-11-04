using Discord;
using Discord.WebSocket;
using System.Diagnostics;

namespace MinerBot_2._0.Services
{
    // Sends a direct message about problems only the bot's owner can fix, to the user in ALERT_USER_ID or else the owner of the Discord application
    public class OwnerAlertService
    {
        public const string RecipientSetting = "ALERT_USER_ID";

        private static readonly TimeSpan ReadyLimit = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ReadyCheckInterval = TimeSpan.FromMilliseconds(100);

        private readonly DiscordSocketClient _client;
        private readonly IConfiguration _configuration;

        public OwnerAlertService(DiscordSocketClient client, IConfiguration configuration)
        {
            _client = client;
            _configuration = configuration;
        }

        public async Task SendAsync(string message)
        {
            await WaitForDiscord();

            var recipient = await _client.GetUserAsync(await GetRecipientId());
            var channel = await recipient.CreateDMChannelAsync();

            await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
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
