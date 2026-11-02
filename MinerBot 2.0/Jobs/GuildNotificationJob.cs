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
        private readonly NotificationState _state;

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
            _services = services;
        }

        [Job("*/5 * * * *")] // Every 5 Minutes
        public async Task NotifyAllGuilds()
        {
            if (!_state.GuildEventsHooked)
            {
                _discord.JoinedGuild += _discord_JoinedGuild;
                _discord.LeftGuild += _discord_LeftGuild;

                _state.GuildEventsHooked = true;
            }

            var newArmor = await GetNewArmor();
            var newMaps = await GetNewMaps();
            var newLobbies = await GetNewLobbies();

            int armorDelivered = 0, mapsDelivered = 0, lobbiesDelivered = 0;

            foreach (var guild in await _DiscordService.GetAllGuilds())
            {
                armorDelivered = Math.Max(armorDelivered, await NotifyNewArmor(guild.armor_channel_id, newArmor));
                mapsDelivered = Math.Max(mapsDelivered, await NotifyNewMap(guild.map_channel_id, newMaps));
                lobbiesDelivered = Math.Max(lobbiesDelivered, await NotifyNewLobby(guild.lobby_channel_id, newLobbies));
            }

            // Only now, so a failure before this point leaves these new for the next run
            _state.Armor.Commit(newArmor, armorDelivered);
            _state.Maps.Commit(newMaps, mapsDelivered);
            _state.Lobbies.Commit(newLobbies, lobbiesDelivered);
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

        public async Task<int> NotifyNewArmor(ulong channelId, List<ArmorStyle>? newArmor)
        {
            if (newArmor == null || newArmor.Count == 0)
                return 0;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;
                await channel.SendMessageAsync(embed: await _ArmorService.NewArmorListEmbed(newArmor));
                return newArmor.Count;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could Send Map Notification to Discord Channel ID: {channelId}\n{e.Message}");
                return 0;
            }
        }

        public async Task<List<Steam.Models.PublishedFileDetailsModel>?> GetNewMaps()
        {
            try
            {
                var mapList = await _SteamAPIService.GetLatestWorkshopItems();

                // GetData returns an empty list when Steam fails
                if (mapList.Count == 0)
                    return null;

                return _state.Maps.FindNew(mapList);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't read the latest maps for notifications\n{e.Message}");
                return null;
            }
        }

        public async Task<int> NotifyNewMap(ulong channelId, List<Steam.Models.PublishedFileDetailsModel>? newMaps)
        {
            if (newMaps == null || newMaps.Count == 0)
                return 0;

            int sentMaps = 0;

            try
            {
                var channel = await _discord.GetChannelAsync(channelId) as IMessageChannel;

                foreach (var (text, embeds) in await _SteamAPIService.GetLatestMapsMessages("__**New & Updated Map(s)!**__", newMaps))
                {
                    await channel.SendMessageAsync(text: text, embeds: embeds);
                    sentMaps += embeds.Length;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could Send Map Notification to Discord Channel ID: {channelId}\n{e.Message}");
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

                return _state.Lobbies.FindNew(lobbyList.Lobbies).Where(lobby => IsInValidVersion(lobby.VersionNumber)).ToList();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Couldn't read the lobby list for notifications\n{e.Message}");
                return null;
            }

            bool IsInValidVersion(uint versionNumber)
            {
                return (versionNumber < 90 && versionNumber >= 87) || versionNumber >= 90;
            }
        }

        public async Task<int> NotifyNewLobby(ulong channelId, List<LobbyInfo>? newLobbies)
        {
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
                Console.WriteLine($"Could Send Lobby Notification to Discord Channel ID: {channelId}\n{e.Message}");
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
