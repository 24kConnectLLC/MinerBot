using Discord;
using Discord.WebSocket;

namespace MinerBot_2._0.Requests
{
    public static class BotPresence
    {
        // What Discord shows under the bot's name, as "Playing Murder Miners on my PC", unless the Owner set a status text
        public const string DefaultGame = "Murder Miners on my PC";
    }

    // The bot's one activity: the default game, or the status text the Owner set from the dashboard, as a custom status,
    // which replaces it. Discord.Net keeps the activity and sends it with each IDENTIFY, which starts a new session, but a
    // RESUME carries none, and a presence frame sent while the socket is down is dropped without an error. So the wanted
    // one is sent again at every connection, which Discord.Net raises after both. The wanted text changes only once
    // MinerBot's database has taken it, and the request worker reads it back at each start.
    public sealed class PresenceKeeper(DiscordSocketClient client, ILogger<PresenceKeeper> logger)
    {
        static readonly TimeSpan SendLimit = TimeSpan.FromSeconds(10);

        // One send at a time, so one that read an older text can't land after a newer one
        readonly SemaphoreSlim sending = new(1, 1);
        string? wanted;

        // Null for the default
        public string? Wanted => Volatile.Read(ref wanted);

        // Before the login: keeps the default, which Discord.Net sends with the first IDENTIFY, as it sends nothing while it
        // has no user, and sends the wanted one again at every connection from then on
        public async Task InitializeAsync()
        {
            client.Connected += OnConnected;
            await SendAsync();
        }

        // Runs on Discord.Net's own task, which waits for it and holds its connect path meanwhile, so the send goes on the
        // thread pool
        internal Task OnConnected()
        {
            _ = Task.Run(SendAsync);
            return Task.CompletedTask;
        }

        // The text the Owner set, or null for the default, once MinerBot's database has it: sent now, and at every connection
        public Task SetAsync(string? text)
        {
            Volatile.Write(ref wanted, text);
            return SendAsync();
        }

        // Never throws: a send that doesn't go now goes with the next connection
        internal async Task SendAsync()
        {
            try
            {
                if (!await sending.WaitAsync(SendLimit))
                    throw new TimeoutException("An earlier send of the bot's status never ended");

                try
                {
                    var text = Wanted;

                    await (text == null ? client.SetGameAsync(BotPresence.DefaultGame, type: ActivityType.Playing) : client.SetCustomStatusAsync(text))
                        .WaitAsync(SendLimit);
                }
                finally
                {
                    sending.Release();
                }
            }
            catch (Exception e)
            {
                logger.LogWarning("The bot's status wasn't sent to Discord ({Problem}). It goes with the next connection.", e.GetType().Name);
            }
        }
    }
}
