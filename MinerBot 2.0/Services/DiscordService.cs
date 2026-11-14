using System.Net;
using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using MinerBot_2._0.Builders;

namespace MinerBot_2._0.Services
{
    public class DiscordService
    {
        // Discord's own names, the way an admin sees them in a channel's settings
        private static readonly Dictionary<ChannelPermission, string> PermissionNames = new()
        {
            [ChannelPermission.ViewChannel] = "View Channel",
            [ChannelPermission.SendMessages] = "Send Messages",
            [ChannelPermission.EmbedLinks] = "Embed Links"
        };

        private readonly IGuildData _guildData;
        private readonly IServiceProvider _services;

        public DiscordService(IGuildData guildData, IServiceProvider services)
        {
            _guildData = guildData;
            _services = services;
        }

        public async Task<Guild> GetGuild(ulong id)
        {
            return await _guildData.GetGuild(id.ToString());
        }

        public async Task<IEnumerable<Guild>> GetAllGuilds()
        {
            return await _guildData.GetAllGuilds();
        }

        public Task SaveGuild(Guild guild) => _guildData.SaveGuild(guild);

        // Posts the announcement in the server's announcements channel, and returns the reply for the admin
        public async Task<string> SendAnnouncement(string message, SocketGuild guild)
        {
            var settings = await GetGuild(guild.Id);

            if (settings == null || settings.announcements_channel_id == Guild.ChannelOff)
                return "Nothing was sent, because this server has no announcements channel. Choose one with /admin set_channels, and include the other channels you want to keep, as any you leave out are turned off.";

            // A deleted channel leaves the cache, so there's no need to ask Discord
            var channel = guild.GetTextChannel(settings.announcements_channel_id);

            if (channel == null)
                return "Nothing was sent, because the announcements channel set for this server no longer exists. Choose a new one with /admin set_channels, and include the other channels you want to keep, as any you leave out are turned off.";

            // Plain text, so it needs no Embed Links
            var missing = MissingPermissions(guild.CurrentUser, channel, ChannelPermission.ViewChannel, ChannelPermission.SendMessages);

            if (missing.Count > 0)
                return $"Nothing was sent, because I'm missing permissions in {channel.Mention}: {string.Join(", ", missing)}. Give me View Channel and Send Messages there, then try again.";

            try
            {
                await channel.SendMessageAsync(message);
            }
            // The cache can be a moment behind, and Discord refuses a bot that's timed out too
            catch (HttpException e) when (e.HttpCode == HttpStatusCode.Forbidden)
            {
                Console.WriteLine($"[Interaction/Warning] Discord refused an announcement in channel {channel.Id} of guild {guild.Id}: {e.Message}");
                return $"Nothing was sent, because Discord didn't let me post in {channel.Mention}. Check that I have View Channel and Send Messages there, then try again.";
            }

            return $"Sent your announcement to {channel.Mention}.";
        }

        public async Task DeleteGuild(ulong guildId)
        {
            if (await GetGuild(guildId) != null)
                await _guildData.DeleteGuild(guildId);
        }

        // The permissions the bot lacks in a channel, from the guild's cached roles and overwrites.
        // Discord.Net works these out the way Discord does, apart from timeouts.
        public static List<string> MissingPermissions(SocketGuildUser bot, IGuildChannel channel, params ChannelPermission[] needed)
        {
            var permissions = bot.GetPermissions(channel);

            return needed.Where(permission => !permissions.Has(permission)).Select(permission => PermissionNames[permission]).ToList();
        }

        // Each kind of notification with its channel. A server that never set its channels has no row, so every kind shows as off.
        public static string ChannelList(Guild? guild)
        {
            return $"- Armor styles: {Channel(guild?.armor_channel_id)}\n- Workshop maps: {Channel(guild?.map_channel_id)}\n- Lobbies: {Channel(guild?.lobby_channel_id)}\n- Announcements: {Channel(guild?.announcements_channel_id)}";

            static string Channel(ulong? id) => id is null || id == Guild.ChannelOff ? "off" : MentionUtils.MentionChannel(id.Value);
        }

        public async Task<Embed> GetInfoEmbed()
        {
            var embed = new EmbedBuilderTemplate()
                .WithTitle($"Info:")
                .WithDescription("If you need assistance with the bot or have a suggestion, please [Contact Us](https://murderminershub.com/contact-us/)")
                .WithFields([
                new EmbedFieldBuilder {
                    Name = "__Learn More About Miner Bot:__",
                    Value = "https://murderminershub.com/discord-bot"
                },
                new EmbedFieldBuilder {
                    Name = "__Check out my Github:__",
                    Value = "https://github.com/24kConnectLLC/MinerBot"
                },
                new EmbedFieldBuilder {
                    Name = "__Visit our Website:__",
                    Value = "https://murderminershub.com"
                },
                new EmbedFieldBuilder {
                    Name = "__Bot & Website Developed by:__",
                    Value = "24kConnect - https://24kconnect.com"
                }
                    ])
                .Build();

            return embed;
        }

        public static Embed GetHelpEmbed()
        {
            var embed = new EmbedBuilderTemplate()
                .WithTitle($"Help:")
                .WithDescription("If you need assistance with the bot or have a suggestion, please [Contact Us](https://murderminershub.com/contact-us/)")
                .WithFields([
                new EmbedFieldBuilder {
                    Name = "__Bot Commands:__",
                    Value =
                         "**/armor list**: Gets a list of all armor styles.\n__Optional__: Filter armor by helmet by adding the helmet name. Use /armor helmets to get all helmet names.\n\n" +
                         "**/armor lookup**: Finds the armor style you are looking for.\n__Required__: Name of armor style. Use /armor list to find all armor styles.\n\n" +
                         "**/armor helmets**: Gets a list of all possible helmet types.\n\n" +
                         "**/more steam_id**: Returns all Steam ID formats for the supplied steam profile.\n__Required__: A Steam Profile Link.\n\n" +
                         "**/murder-miners lobbies**: Finds all Lobbies currently publicly open in Murder Miners.\n\n" +
                         "**/maps latest**: Returns up to 5 of the latest Maps on the Murder Miners Workshop, one Map per page.\n\n" +
                         "**/maps lookup**: Returns a detailed result of the Murder Miners Workshop Map Link you provided.\n__Required__: A Steam Workshop Link or ID of the Map.\n\n" +
                         "**/more info**: More Info and Credits about Miner Bot.\n\n" +
                         "**/help**: Shows this list."
                },
                new EmbedFieldBuilder {
                    Name = "__Admin Commands:__",
                    Value =
                         "**/admin set_channels**: Sets where I post each kind of notification. Any kind you leave out is turned off.\n\n" +
                         "**/admin show_channels**: Shows where I post each kind of notification.\n\n" +
                         "**/admin announce**: Creates an announcement."
                },
                new EmbedFieldBuilder {
                    Name = "__Check out my Github:__",
                    Value = "https://github.com/24kConnectLLC/MinerBot"
                },
                new EmbedFieldBuilder {
                    Name = "__Visit our Website:__",
                    Value = "https://murderminershub.com"
                },
                new EmbedFieldBuilder {
                    Name = "__Bot & Website Developed by:__",
                    Value = "24kConnect - https://24kconnect.com"
                }
                    ])
                .Build();

            return embed;
        }
    }
}
