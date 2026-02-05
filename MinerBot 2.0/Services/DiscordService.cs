using DataAccessLibrary.Discord;
using DataAccessLibrary.Discord.Models;
using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Services
{
    public class DiscordService
    {
        private readonly IGuildData _guildData;
        private readonly DiscordSocketClient _discord;
        private readonly IServiceProvider _services;

        public DiscordService(IGuildData guildData, IServiceProvider services)
        {
            _guildData = guildData;
            _discord = services.GetRequiredService<DiscordSocketClient>();
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

        public async Task UpdateGuild(Guild guild)
        {
            if (await GetGuild(guild.guild_id) != null)
                await _guildData.UpdateGuild(guild);
            else
                await _guildData.InsertGuild(guild);
        }

        public async Task InsertGuild(Guild guild)
        {
            await _guildData.InsertGuild(guild);
        }

        public async Task SendAnnouncement(string message, ulong guildId)
        {
            var guild = await GetGuild(guildId);
            var channel = await _discord.GetChannelAsync(guild.announcements_channel_id) as IMessageChannel;
            await channel.SendMessageAsync(message);
        }

        public async Task DeleteGuild(ulong guildId)
        {
            if (await GetGuild(guildId) != null)
                await _guildData.DeleteGuild(guildId);
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
    }
}
