using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Services;
using MinerBot_2._0.Attributes;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord.Models;

namespace MinerBot_2._0.Jobs
{
    public class GuildNotificationJob
    {
        // Dependency Injection will fill this value in for us
        private readonly SteamAPIService _SteamAPIService;
        private readonly ArmorService _ArmorService;
        private readonly DiscordService _DiscordService;
        private readonly NotificationState _state;
        private readonly TimeProvider _clock;

        private readonly DiscordSocketClient _discord;
        private readonly IServiceProvider _services;

        // Constructor injection is also a valid way to access the dependencies
        public GuildNotificationJob(ILogger<GuildNotificationJob> logger, IServiceProvider services)
        {
            _discord = services.GetRequiredService<DiscordSocketClient>();
            _ArmorService = services.GetRequiredService<ArmorService>();
            _SteamAPIService = services.GetRequiredService<SteamAPIService>();
            _DiscordService = services.GetRequiredService<DiscordService>();
            _state = services.GetRequiredService<NotificationState>();
            _clock = services.GetRequiredService<TimeProvider>();
            _services = services;
        }

        [Job("*/5 * * * *")] // Every 5 Minutes
        public async Task NotifyAllGuilds()
        {
            // One time for the whole run, so the map cooldown is checked and recorded with the same one
            var now = _clock.GetUtcNow();

            var newArmor = await GetNewArmor();
            var newMaps = await GetNewMaps(now);
            var newLobbies = await GetNewLobbies();

            int armorDelivered = 0, mapsDelivered = 0, lobbiesDelivered = 0;

            var guilds = (await _DiscordService.GetAllGuilds()).ToList();

            foreach (var guild in guilds)
            {
                armorDelivered = Math.Max(armorDelivered, await NotifyNewArmor(guild.guild_id, guild.armor_channel_id, newArmor));
                mapsDelivered = Math.Max(mapsDelivered, await NotifyNewMap(guild.guild_id, guild.map_channel_id, newMaps));
                lobbiesDelivered = Math.Max(lobbiesDelivered, await NotifyNewLobby(guild.guild_id, guild.lobby_channel_id, newLobbies));
            }

            // Only now, so a failure before this point leaves these new for the next run.
            // A kind that no server has on has nobody to wait for, so its new items are all recorded as seen. Otherwise they'd be
            // tried again for an hour and then logged as a failure, and the first server to turn that kind on would get them as new.
            // Maps that no server wants aren't posted, so they start no cooldown.
            bool mapsOn = AnyServerHasItOn(guild => guild.map_channel_id);

            _state.Armor.Commit(newArmor, AnyServerHasItOn(guild => guild.armor_channel_id) ? armorDelivered : newArmor?.Count ?? 0);
            _state.Maps.Commit(newMaps, mapsOn ? mapsDelivered : newMaps?.Count ?? 0, postedAt: mapsOn ? now : null);
            _state.Lobbies.Commit(newLobbies, AnyServerHasItOn(guild => guild.lobby_channel_id) ? lobbiesDelivered : newLobbies?.Count ?? 0);

            bool AnyServerHasItOn(Func<Guild, ulong> channelId) => guilds.Any(guild => channelId(guild) != Guild.ChannelOff);
        }

        // A null list means that the source couldn't be read, so these three leave it for the next run.
        // The NotifyNew methods return how many of the items reached the channel.
        public async Task<List<ArmorStyle>?> GetNewArmor()
        {
            try
            {
                var armorList = await _ArmorService.GetArmorList();

                if (armorList.Count == 0)
                    return null;

                return _state.Armor.FindNew(armorList);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't read the armor list for notifications\n{e.Message}");
                return null;
            }
        }

        public async Task<int> NotifyNewArmor(ulong guildId, ulong channelId, List<ArmorStyle>? newArmor)
        {
            // A kind the server turned off isn't a failure
            if (channelId == Guild.ChannelOff)
                return 0;

            if (newArmor == null || newArmor.Count == 0)
                return 0;

            int sentArmor = 0;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;

                foreach (var (embed, armorCount) in ArmorService.NewArmorListEmbeds(newArmor))
                {
                    await channel.SendMessageAsync(embed: embed);
                    sentArmor += armorCount;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't send the armor notification to channel {channelId} of guild {guildId}\n{e.Message}");
            }

            return sentArmor;
        }

        public async Task<List<MapPost>?> GetNewMaps(DateTimeOffset now)
        {
            try
            {
                var mapList = await _SteamAPIService.GetLatestWorkshopItems();

                // GetData returns an empty list when Steam fails
                if (mapList.Count == 0)
                    return null;

                return _state.Maps.FindNew(mapList, now);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't read the latest maps for notifications\n{e.Message}");
                return null;
            }
        }

        public async Task<int> NotifyNewMap(ulong guildId, ulong channelId, List<MapPost>? newMaps)
        {
            if (channelId == Guild.ChannelOff)
                return 0;

            if (newMaps == null || newMaps.Count == 0)
                return 0;

            int sentMaps = 0;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;

                foreach (var (text, embeds) in await _SteamAPIService.GetLatestMapsMessages(newMaps.Select(post => (post.Map, post.IsNew)).ToList()))
                {
                    await channel.SendMessageAsync(text: text, embeds: embeds);
                    sentMaps += embeds.Length;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't send the map notification to channel {channelId} of guild {guildId}\n{e.Message}");
            }

            return sentMaps;
        }

        public async Task<List<LobbyInfo>?> GetNewLobbies()
        {
            try
            {
                var lobbyList = await _SteamAPIService.GetLobbyList();

                if (!lobbyList.Available)
                    return null;

                return _state.Lobbies.FindNew(lobbyList.Lobbies).Where(lobby => GameVersions.Notifies(lobby.VersionNumber)).ToList();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't read the lobby list for notifications\n{e.Message}");
                return null;
            }
        }

        public async Task<int> NotifyNewLobby(ulong guildId, ulong channelId, List<LobbyInfo>? newLobbies)
        {
            if (channelId == Guild.ChannelOff)
                return 0;

            if (newLobbies == null || newLobbies.Count == 0)
                return 0;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;
                await channel.SendMessageAsync(
                    embed: await _SteamAPIService.LobbyListEmbed(await _SteamAPIService.GetLobbiesDisplay(newLobbies, hideEmpty: true), type: "__NEW__"));
                return newLobbies.Count;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't send the lobby notification to channel {channelId} of guild {guildId}\n{e.Message}");
                return 0;
            }
        }

        public async Task LeaveForPerms()
        {
            if (false)
            {
                foreach (var guild in _discord.Guilds)
                {
                    try
                    {
                        var channel = guild.GetTextChannel(guild.PublicUpdatesChannel == null ? guild.SystemChannel.Id : guild.PublicUpdatesChannel.Id);

                        await channel.SendMessageAsync("MinerBot 2.0 is Here with lots of new features. Learn more at: https://murderminershub.com/discord-bot \nI will be temporarily leaving as I don't have sufficient permissions... Please visit the link above to re-add me.\nThanks!");
                    }
                    catch
                    {
                        try
                        {
                            var channel = guild.GetTextChannel(guild.TextChannels.Where(a => a.Name.Contains("bot")).Select(a => a.Id).First());
                            await channel.SendMessageAsync("MinerBot 2.0 is Here with lots of new features. Learn more at: https://murderminershub.com/discord-bot \nI will be temporarily leaving as I don't have sufficient permissions... Please visit the link above to re-add me.\nThanks!");
                        }

                        catch
                        {
                            Console.WriteLine("Could not write message to channel");
                        }
                    }

                    try
                    {
                        await Task.Delay(10000);
                        await guild.LeaveAsync();
                        Console.WriteLine($"Left Server: {guild.Name}");
                    }
                    catch
                    {
                        Console.WriteLine($"Had issues leaving the server {guild.Name}");
                    }
                }
            }
        }
    }
}
