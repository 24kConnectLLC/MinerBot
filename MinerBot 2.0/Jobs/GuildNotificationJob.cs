using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Services;
using MinerBot_2._0.Attributes;
using SteamWebAPIAccess.Models;
using DataAccessLibrary.Armor.Models;

namespace MinerBot_2._0.Jobs
{
    public class GuildNotificationJob
    {
        // Dependency Injection will fill this value in for us
        private readonly SteamAPIService _SteamAPIService;
        private readonly ArmorService _ArmorService;
        private readonly DiscordService _DiscordService;

        private readonly DiscordSocketClient _discord;
        private readonly IServiceProvider _services;

        private static List<ulong> armorIDList = new List<ulong>(),
                                     mapIDList = new List<ulong>(),
                                   lobbyIDList = new List<ulong>();

        private static bool FirstRun = true;

        // Constructor injection is also a valid way to access the dependencies
        public GuildNotificationJob(ILogger<GuildNotificationJob> logger, IServiceProvider services)
        {
            _discord = services.GetRequiredService<DiscordSocketClient>();
            _ArmorService = services.GetRequiredService<ArmorService>();
            _SteamAPIService = services.GetRequiredService<SteamAPIService>();
            _DiscordService = services.GetRequiredService<DiscordService>();
            _services = services;
        }

        [Job("*/5 * * * *")] // Every 5 Minutes
        public async Task NotifyAllGuilds()
        {
            if (FirstRun)
            {
                await GetNewArmor();
                await GetNewMaps();
                await GetNewLobbies();

                _discord.JoinedGuild += _discord_JoinedGuild;
                _discord.LeftGuild += _discord_LeftGuild;

                FirstRun = false;
            }
            else
            {
                var newArmor = await GetNewArmor();
                var newMaps = await GetNewMaps();
                var newLobbies = await GetNewLobbies();

                foreach (var guild in await _DiscordService.GetAllGuilds())
                {
                    await NotifyNewArmor(guild.armor_channel_id, newArmor);
                    await NotifyNewMap(guild.map_channel_id, newMaps);
                    await NotifyNewLobby(guild.lobby_channel_id, newLobbies);
                }
            }
        }

        private async Task _discord_LeftGuild(SocketGuild arg)
        {
            await _DiscordService.DeleteGuild(arg.Id);
        }

        private async Task _discord_JoinedGuild(SocketGuild arg)
        {
            try
            {
                var bot = arg.GetUser(_discord.CurrentUser.Id);
                var permissions = bot.GuildPermissions;
                if (permissions.Administrator)
                {
                    var channel = arg.GetTextChannel(arg.PublicUpdatesChannel == null ? arg.SystemChannel.Id : arg.PublicUpdatesChannel.Id);
                    await channel.SendMessageAsync("Set up your channels for notifications using the /more setchannels command!\nFirst, make sure that I can access the channels.");
                }
            }
            catch
            {
                Console.WriteLine("Couldn't send welcome message!");
            }
        }

        public async Task<List<ArmorStyle>> GetNewArmor()
        {
            var armorList = _ArmorService.GetArmorList().Result;

            if (armorList.Count == 0)
                return null;

            var newIDList = armorIDList;
            var newArmorList = new List<ArmorStyle>();

            foreach (var armor in armorList)
            {
                if (!armorIDList.Contains((ulong)armor.ID))
                {
                    newArmorList.Add(armor);
                }

                newIDList.Add((ulong)armor.ID);
            }

            armorIDList = newIDList;

            return newArmorList;
        }

        public async Task NotifyNewArmor(ulong channelId, List<ArmorStyle> newArmor)
        {
            if (newArmor == null || newArmor.Count == 0)
                return;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;
                await channel.SendMessageAsync(embed: await _ArmorService.NewArmorListEmbed(newArmor));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could Send Map Notification to Discord Channel ID: {channelId}\n{e.Message}");
            }
        }

        public async Task<List<Steam.Models.PublishedFileDetailsModel>> GetNewMaps()
        {
            var mapList = _SteamAPIService.GetLatestWorkshopItems().Result;

            if (mapList.Count == 0)
                return null;

            var newIDList = mapIDList;
            var newMapList = new List<Steam.Models.PublishedFileDetailsModel>();

            foreach (var map in mapList)
            {
                if (!mapIDList.Contains(map.PublishedFileId))
                {
                    newMapList.Add(map);
                }

                newIDList.Add(map.PublishedFileId);
            }

            mapIDList = newIDList;

            return newMapList;
        }

        public async Task NotifyNewMap(ulong channelId, List<Steam.Models.PublishedFileDetailsModel> newMaps)
        {
            if (newMaps == null || newMaps.Count == 0)
                return;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;
                await channel.SendMessageAsync(
                    text: "__**New & Updated Map(s)!**__",
                    embeds: await _SteamAPIService.GetLatestMapsEmbeds(newMaps));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could Send Map Notification to Discord Channel ID: {channelId}\n{e.Message}");
            }
        }

        public async Task<List<LobbyInfo>> GetNewLobbies()
        {
            var lobbyList = await _SteamAPIService.GetLobbyList();

            if (lobbyList == null || lobbyList.Count == 0)
                return null;

            var newIDList = lobbyIDList;
            var newLobbyList = new List<LobbyInfo>();

            foreach (var lobby in lobbyList)
            {
                if (!lobbyIDList.Contains(lobby.LobbyId) && IsInValidVersion(lobby.VersionNumber))
                {
                    newLobbyList.Add(lobby);
                }

                newIDList.Add(lobby.LobbyId);
            }

            lobbyIDList = newIDList;

            return newLobbyList;

            bool IsInValidVersion(uint versionNumber)
            {
                return (versionNumber < 90 && versionNumber >= 87) || versionNumber >= 90;
            }
        }

        public async Task NotifyNewLobby(ulong channelId, List<LobbyInfo> newLobbies)
        {
            if (newLobbies == null || newLobbies.Count == 0)
                return;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;
                await channel.SendMessageAsync(
                    embed: await _SteamAPIService.LobbyListEmbed(await _SteamAPIService.GetLobbiesDisplay(newLobbies, hideEmpty: true), type: "__NEW__"));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could Send Lobby Notification to Discord Channel ID: {channelId}\n{e.Message}");
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
