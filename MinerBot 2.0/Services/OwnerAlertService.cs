using Discord;
using Discord.WebSocket;
using System.Diagnostics;

namespace MinerBot_2._0.Services
{
    // Sends a direct message to the owner of the Discord application, for problems only they can fix
    public class OwnerAlertService
    {
        private static readonly TimeSpan ReadyLimit = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ReadyCheckInterval = TimeSpan.FromMilliseconds(100);

        private readonly DiscordSocketClient _client;

        public OwnerAlertService(DiscordSocketClient client)
        {
            _client = client;
        }

        public async Task SendAsync(string message)
        {
            await WaitForDiscord();

            var application = await _client.GetApplicationInfoAsync();

            // The owner of an application that belongs to a team is only a placeholder for the team
            var ownerId = application.Team?.OwnerUserId ?? application.Owner.Id;

            var owner = await _client.GetUserAsync(ownerId);
            var channel = await owner.CreateDMChannelAsync();

            await channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
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
