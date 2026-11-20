using Discord;
using Discord.WebSocket;
using MinerBot_2._0.Services;
using MinerBot_2._0.Attributes;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Discord.Models;
using MinerBot_2._0.Requests;
using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    public class GuildNotificationJob
    {
        // Dependency Injection will fill this value in for us
        private readonly SteamAPIService _SteamAPIService;
        private readonly ArmorService _ArmorService;
        private readonly DiscordService _DiscordService;
        private readonly NotificationState _state;
        private readonly PostsPause _pause;
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
            _pause = services.GetRequiredService<PostsPause>();
            _clock = services.GetRequiredService<TimeProvider>();
            _services = services;
        }

        // A run that posted as usual has no detail, one that held its posts says why, and one that didn't find again maps that
        // waited says how many, which the Bot page shows
        [Job("*/5 * * * *")] // Every 5 Minutes
        public async Task<string?> NotifyAllGuilds()
        {
            // One time for the whole run, so the map cooldown is checked and recorded with the same one
            var now = _clock.GetUtcNow();

            var newArmor = await GetNewArmor();
            var newMaps = await GetNewMaps(now);
            var newLobbies = await GetNewLobbies();

            // While Discord has sent none of the bot's servers, no post can be checked against its server: before the
            // gateway's first READY since the start Discord.Net has none of them, and from a READY, at a start or a new
            // session, nothing of them but their IDs until they arrive, which waits for the gateway if it drops meanwhile.
            // The new items wait for a run after that, and this one doesn't count against them, with a line in the log when
            // it had any.
            if (_discord.CurrentUser == null || (_discord.Guilds.Count > 0 && _discord.Guilds.All(guild => guild.Name == null)))
            {
                if (newArmor is { Count: > 0 } || newMaps is { Count: > 0 } || newLobbies is { Count: > 0 })
                    Console.WriteLine("Couldn't send the notifications yet, because Discord hasn't sent the bot's servers");

                return null;
            }

            // Paused from the dashboard, or not known yet: the new items wait, uncounted, for the first run that posts.
            // Nothing is committed, so nothing is marked seen and no run counts against an item, and the first good read
            // still takes its baseline. The maps found are kept either way, so the first run that posts reads deep enough
            // for them.
            if (_pause.Holds(out var known))
            {
                if (known)
                    return Held(newArmor, newMaps, newLobbies);

                _state.Maps.Hold(newMaps);
                return "posts wait until the bot has read whether they're paused";
            }

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
            var mapsNotFound = _state.Maps.Commit(newMaps, mapsOn ? mapsDelivered : newMaps?.Count ?? 0, postedAt: mapsOn ? now : null);
            _state.Lobbies.Commit(newLobbies, AnyServerHasItOn(guild => guild.lobby_channel_id) ? lobbiesDelivered : newLobbies?.Count ?? 0);

            // Maps that waited and that this run's read didn't find again are never posted, which the log and the Bot page say
            if (mapsNotFound > 0)
            {
                var detail = MapsNotFound(mapsNotFound);
                Console.WriteLine(detail);
                return detail;
            }

            return null;

            bool AnyServerHasItOn(Func<Guild, ulong> channelId) => guilds.Any(guild => channelId(guild) != Guild.ChannelOff);
        }

        static string MapsNotFound(int count) => count == 1
            ? "1 map that waited wasn't in Steam's latest list any more, so it wasn't posted: it was hidden meanwhile, or more maps were updated while posts waited than the read reaches"
            : string.Create(CultureInfo.InvariantCulture,
                $"{count} maps that waited weren't in Steam's latest list any more, so they weren't posted: they were hidden meanwhile, or more maps were updated while posts waited than the read reaches");

        // What a paused run found, which waits for the first run after the resume. The approved armor and the open lobbies are
        // read whole, but the maps only from the top of Steam's list, so the ones found are kept to read deep enough for them.
        string Held(List<ArmorStyle>? armor, List<MapPost>? maps, List<LobbyInfo>? lobbies)
        {
            _state.Maps.Hold(maps);

            return string.Create(CultureInfo.InvariantCulture,
                $"posts are paused, so this run posted nothing (found {Count(armor?.Count, "new armor style")}, {Count(maps?.Count, "new or updated map")} and {Count(lobbies?.Count, "new lobby", "new lobbies")})");
        }

        static string Count(int? count, string one, string? many = null) =>
            string.Create(CultureInfo.InvariantCulture, $"{count ?? 0} {(count == 1 ? one : many ?? one + "s")}");

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

            if (ChannelOf("armor", guildId, channelId) is not { } channel)
                return 0;

            int sentArmor = 0;

            try
            {
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
                var mapList = await _SteamAPIService.GetLatestWorkshopItems(_state.Maps.ReadCount);

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

            if (ChannelOf("map", guildId, channelId) is not { } channel)
                return 0;

            int sentMaps = 0;

            try
            {
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

            if (ChannelOf("lobby", guildId, channelId) is not { } channel)
                return 0;

            try
            {
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

        // The row's channel, by DiscordService.PostChannel's rule: a channel of the server the row names that Discord.Net
        // counts as a text channel, from the cache, so a row can't send a post to another server's channel, and no channel is
        // looked up over Discord's API. A server the bot isn't in, one Discord reports unavailable, in an outage, or one it
        // hasn't sent yet gets no post either. Each of these is a post that couldn't be sent, with a line in the log: as for a
        // post that failed, its items are tried again at the next run only when no server got them.
        private SocketTextChannel? ChannelOf(string kind, ulong guildId, ulong channelId)
        {
            if (DiscordService.PostChannel(_discord, guildId, channelId, out var problem) is { } channel)
                return channel;

            var because = problem switch
            {
                PostProblem.NotInServer => "the bot isn't in that server",
                PostProblem.ServerUnavailable => "Discord reports that server unavailable",
                PostProblem.ServerNotSent => "Discord hasn't sent that server yet",
                _ => "that channel isn't in that server"
            };

            Console.WriteLine($"Couldn't send the {kind} notification to channel {channelId} of guild {guildId}, because {because}");
            return null;
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
