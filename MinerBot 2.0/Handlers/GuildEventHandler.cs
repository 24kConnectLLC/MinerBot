using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Services;

namespace MinerBot_2._0.Handlers;

// Joins and leaves can come as soon as the bot connects, so these are attached before it logs in
public class GuildEventHandler
{
    // Plain text, so the welcome itself needs no Embed Links
    public const string WelcomeMessage = "Thanks for adding me! A server admin can choose where I post new armor styles, workshop maps, lobbies and announcements with /admin set_channels, and see the current choices with /admin show_channels. I need View Channel, Send Messages and Embed Links in those channels.";

    private readonly DiscordSocketClient _client;
    private readonly DiscordService _DiscordService;

    public GuildEventHandler(DiscordSocketClient client, DiscordService discordService)
    {
        _client = client;
        _DiscordService = discordService;
    }

    public void Initialize()
    {
        _client.JoinedGuild += JoinedGuildAsync;
        _client.LeftGuild += LeftGuildAsync;
    }

    private async Task LeftGuildAsync(SocketGuild guild)
    {
        await _DiscordService.DeleteGuild(guild.Id);
    }

    private async Task JoinedGuildAsync(SocketGuild guild)
    {
        // Discord lists the bot's own member with a guild even with only the Guilds intent, so its roles are known here,
        // unless Discord sends the guild as unavailable with nothing but its id, as it can during an outage. Discord.Net takes
        // that as a join even for a server the bot was already in, and raises no event once the rest arrives, so that guild gets no welcome.
        var bot = guild.CurrentUser;

        if (bot == null)
        {
            Console.WriteLine($"Discord sent guild {guild.Id} as unavailable, which happens during an outage, so no welcome message was sent to it");
            return;
        }

        try
        {
            var channel = FindWelcomeChannel(guild, bot);

            if (channel == null)
            {
                Console.WriteLine($"Couldn't welcome guild {guild.Id}, there's no text channel where the bot can view and send messages");
                return;
            }

            await channel.SendMessageAsync(WelcomeMessage);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't send the welcome message to guild {guild.Id}\n{e.Message}");
        }
    }

    // The server's public updates channel, then its system channel, then its text channels from the top, whichever the bot can post in first
    private static SocketTextChannel? FindWelcomeChannel(SocketGuild guild, SocketGuildUser bot)
    {
        // Discord.Net's TextChannels holds voice, stage, thread and announcement channels too, as their classes derive from its text channel
        var textChannels = guild.TextChannels.Where(channel => channel.ChannelType == ChannelType.Text).OrderBy(channel => channel.Position).ThenBy(channel => channel.Id);

        return new[] { guild.PublicUpdatesChannel, guild.SystemChannel }
            .Concat(textChannels)
            .FirstOrDefault(channel => channel != null && DiscordService.MissingPermissions(bot, channel, ChannelPermission.ViewChannel, ChannelPermission.SendMessages).Count == 0);
    }
}
