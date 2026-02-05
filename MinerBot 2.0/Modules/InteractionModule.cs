using Discord;
using Discord.Interactions;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Services;
using System.ComponentModel;

namespace MinerBot_2._0.Modules;

// Interaction modules must be public and inherit from an IInteractionModuleBase
public class InteractionModule : InteractionModuleBase<SocketInteractionContext>
{
    // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
    // Dependency Injection will fill this value in for us
    public PictureService _PictureService { get; set; }
    public AnnounceService _AnnounceService { get; set; }
    public SteamAPIService _SteamAPIService { get; set; }
    public ArmorService _ArmorService { get; set; }
    public DiscordService _DiscordService { get; set; }

    private InteractionHandler _handler;

    // Constructor injection is also a valid way to access the dependencies
    public InteractionModule(InteractionHandler handler)
    {
        _handler = handler;
    }

    // You can use a number of parameter types in your Slash Command handlers (string, int, double, bool, IUser, IChannel, IMentionable, IRole, Enums) by default. Optionally,
    // you can implement your own TypeConverters to support a wider range of parameter types. For more information, refer to the library documentation.
    // Optional method parameters(parameters with a default value) also will be displayed as optional on Discord.

    // [Summary] lets you customize the name and the description of a parameter

    //[SlashCommand("echo", "Repeat the input")]
    //public async Task Echo(string echo, [Summary(description: "mention the user")] bool mention = false)
    //    => await RespondAsync(echo + (mention ? Context.User.Mention : string.Empty));

    //[SlashCommand("ping", "Pings the bot and returns its latency.")]
    //public async Task GreetUserAsync()
    //    => await RespondAsync(text: $":ping_pong: It took me {Context.Client.Latency}ms to respond to you!", ephemeral: true);

    //[SlashCommand("bitrate", "Gets the bitrate of a specific voice channel.")]
    //public async Task GetBitrateAsync([ChannelTypes(ChannelType.Voice, ChannelType.Stage)] IChannel channel)
    //    => await RespondAsync(text: $"This voice channel has a bitrate of {(channel as IVoiceChannel).Bitrate}");

    [RateLimit(3)]
    [Group("murder-miners", "Commands for Murder Miners")]

    public class MurderMinersGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }

        [SlashCommand("lobbies", "Finds all Lobbies currently publically open in Murder Miners.")]
        public async Task GetLobbyList()
        {
            await RespondAsync(embed: await _SteamAPIService.LobbyListEmbed());
        }

        //[Discord.Interactions.RequireOwner]
        //[Group("admin", "Admin Commands for Murder Miners")]

        //public class MurderMinersAdminGroupModule() : InteractionModuleBase<SocketInteractionContext>
        //{
        //    public SteamAPIService _SteamAPIService { get; set; }

        //    [SlashCommand("create_lobby", "Creates a Murder Miners Lobby.")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task CreateLobby()
        //    {
        //        await RespondAsync("Lobby has been created! " + await _SteamAPIService.CreateLobby());
        //    }

        //    [SlashCommand("find_users", "Find users in a lobby.")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task FindUsers(string lobbyID)
        //    {
        //        await RespondAsync(await _SteamAPIService.GetUsersInLobby(ulong.Parse(lobbyID)));
        //    }

        //    [SlashCommand("kick_user", "Kicks a user from a lobby.")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task KickUser(string userID, string lobbyID)
        //    {
        //        await RespondAsync(await _SteamAPIService.KickUserFromLobby(ulong.Parse(userID), ulong.Parse(lobbyID)));
        //    }

        //    [SlashCommand("send_message", "Sends a message to a lobby.")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task SendMessageToLobby(string lobbyID, string message)
        //    {
        //        await RespondAsync(await _SteamAPIService.SendMessageToLobby(ulong.Parse(lobbyID), message));
        //    }

        //    [SlashCommand("invite_user", "Invite a user to a lobby.")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task InviteUserToLobby(string lobbyID, string userID)
        //    {
        //        await RespondAsync(await _SteamAPIService.InviteUserToLobby(ulong.Parse(lobbyID), ulong.Parse(userID)));
        //    }

        //    [SlashCommand("join_lobby", "Join a lobby.")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task JoinLobby(string lobbyID)
        //    {
        //        await RespondAsync(await _SteamAPIService.JoinLobby(ulong.Parse(lobbyID)));
        //    }

        //    [SlashCommand("leave_lobby", "Leave a lobby")]
        //    [RequireUserPermission(GuildPermission.BanMembers)]
        //    public async Task LeaveLobby(string lobbyID)
        //    {
        //        await RespondAsync(await _SteamAPIService.LeaveLobby(ulong.Parse(lobbyID)));
        //    }
        //}
    }

    [RateLimit(5)]
    [Group("armor", "Commands for Armor Styles")]
    public class ArmorGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public ArmorService _ArmorService { get; set; }

        [SlashCommand("list", "Gets a list of all armor styles.")]
        public async Task ArmorList([Summary(description: "Optional: Filter armor by helmet by adding the helmet name.")] HelmentType helmetType = HelmentType.All)
        {
            var armors = await _ArmorService.GetArmorListNames(helmetType);

            var itemsInPage = armors.Count();

            if (itemsInPage > _ArmorService.ItemsPerPage)
            {
                var component = new ComponentBuilder()
                .WithButton("Previous", $"armorlist:{Context.Interaction.User.Id},0,previous,{helmetType}", disabled: true)
                .WithButton("Next", $"armorlist:{Context.Interaction.User.Id},0,next,{helmetType}")
                .Build();

                await RespondAsync(embed: await _ArmorService.ArmorListEmbed(helmetType), components: component);
            }
            else
                await RespondAsync(embed: await _ArmorService.ArmorListEmbed(helmetType));
        }

        [SlashCommand("lookup", "Finds the armor style you are looking for. Use /armor list to find all armor styles.")]
        public async Task GetArmor([Summary(description: "Required: Name of armor style.")] [Autocomplete(typeof(ArmorAutocompleteHandler))] string armorName)
        {
            var armor = await _ArmorService.GetArmorStyle(armorName);
            if (armor == null)
            {
                await RespondAsync(embed: await _ArmorService.ErrorEmbed());
                return;
            }

            var result = await _ArmorService.ArmorEmbed(armor);
            await RespondWithFileAsync(result.ArmorImage, embed: result.ArmorEmbed);
        }

        //[SlashCommand("helmets", "Gets a list of all possible helmet types.")]
        //public async Task GetHelmets()
        //{
        //    await RespondAsync(embed: await _ArmorService.GetHelmetsEmbed());
        //}
    }

    [RequireContext(ContextType.Guild)]
    [Group("maps", "Commands for Steam Workshop Maps.")]
    public class WorkshopGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }

        [RateLimit(5)]
        [SlashCommand("latest", "Returns the 5 detailed results of the most Newst Maps on the Murder Miners Workshop.")]
        public async Task GetLatestWorkshopItems()
        {
            var component = new ComponentBuilder()
                .WithButton("Previous", $"maplist:{Context.Interaction.User.Id},0,previous", disabled: true)
                .WithButton("Next", $"maplist:{Context.Interaction.User.Id},0,next")
                .Build();

            await RespondAsync("__**Latest Maps: (1/5)**__", embed: await _SteamAPIService.GetLatestMapsEmbed(),
                options: new RequestOptions() { Timeout = 10 },
                components: component);
        }

        [RateLimit(3)]
        [SlashCommand("lookup", "Returns a detailed result of the Murder Miners Workshop Map Link you provided.")]
        public async Task GetWorkshopItem([Summary(description: "Required: A Steam Workshop Link of the Map.")] string mapURL)
        {
            await RespondAsync(embed: await _SteamAPIService.GetMapEmbed(mapURL));
        }
    }

    [RateLimit(3)]
    [RequireContext(ContextType.Guild)]
    [Group("more", "Other Useful Commands.")]
    public class MoreGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }
        public DiscordService _DiscordService { get; set; }

        [SlashCommand("steam_id", "Returns all Steam ID formats for the supplied steam profile.")]
        public async Task GetSteamID([Summary(description: "Required: A USER Steam Profile Link.")] string profileURL)
        {
            await RespondAsync(embed: await _SteamAPIService.SteamIDEmbed(profileURL));
        }

        [SlashCommand("info", "More Info and Credits about Miner Bot")]
        public async Task Info()
        {
            await RespondAsync(embed: await _DiscordService.GetInfoEmbed());
        }
    }

    [RateLimit(5)]
    [RequireContext(ContextType.Guild)]
    [Group("admin", "Admin Only Commands.")]
    public class AdminGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }
        public DiscordService _DiscordService { get; set; }

        [RequireUserPermission(GuildPermission.Administrator)]
        [SlashCommand("set_channels", "Sets Channels for Notifications. Scans every 5 minutes (except announce command). Admin Only.")]
        public async Task SetNotificationChannel(IMessageChannel armorChannel, IMessageChannel mapChannel, IMessageChannel lobbyChannel, IMessageChannel announcementsChannel)
        {
            await _DiscordService.UpdateGuild(new DataAccessLibrary.Discord.Models.Guild()
            {
                guild_id = Context.Guild.Id,
                armor_channel_id = armorChannel.Id,
                map_channel_id = mapChannel.Id,
                lobby_channel_id = lobbyChannel.Id,
                announcements_channel_id = announcementsChannel.Id
            });
            await RespondAsync("Thanks for setting the channels!");
        }

        [RequireUserPermission(GuildPermission.Administrator)]
        [SlashCommand("announce", "Creates an announcement. Admin Only.")]
        public async Task Announce(string message)
        {
            //await AnnounceService.Announce("Announcement", message);
            await _DiscordService.SendAnnouncement(message, Context.Guild.Id);
            await RespondAsync("Announcement Sent!");
        }

        //[Discord.Interactions.RequireOwner]
        //[SlashCommand("reconnect", "Reconnects to Steam")]
        //public async Task ReconnectToSteam()
        //{
        //    await RespondAsync(_SteamAPIService.ReconnectToSteam());
        //}

        //[Discord.Interactions.RequireOwner]
        //[SlashCommand("disconnect", "Disconnects from Steam")]
        //public async Task DisconnectFromSteam()
        //{
        //    await RespondAsync(_SteamAPIService.DisconnectFromSteam());
        //}
    }

    // With the Attribute DoUserCheck you can make sure that only the user this button targets can click it. This is defined by the first wildcard: *.
    // See Attributes/DoUserCheckAttribute.cs for elaboration.

    [DoUserCheck]
    [ComponentInteraction("armorlist:*,*,*,*", ignoreGroupNames: true, TreatAsRegex = false)]
    public async Task ChangeArmorListPage(string id, int page, string direction, string helmetType)
    {
        page += (direction == "previous" ? -1 : 1);

        var helmetTypeE = Enum.Parse<HelmentType>(helmetType);

        var armors = await _ArmorService.GetArmorListNames(helmetTypeE, page);

        var itemsInPage = armors.Count();

        var embed = await _ArmorService.ArmorListEmbed(helmetTypeE, armors, page);

        var component = new ComponentBuilder()
                .WithButton("Previous", $"armorlist:{Context.Interaction.User.Id},{page},previous,{helmetTypeE}", disabled: (page == 0 ? true : false))
                .WithButton("Next", $"armorlist:{Context.Interaction.User.Id},{page},next,{helmetTypeE}", disabled: (itemsInPage < _ArmorService.ItemsPerPage ? true : false))
                .Build();

        var interaction = Context.Interaction as IComponentInteraction;
        await interaction.UpdateAsync(a => { a.Embed = embed; a.Components = component; });
    }

    [DoUserCheck]
    [ComponentInteraction("maplist:*,*,*", ignoreGroupNames: true, TreatAsRegex = false)]
    public async Task ChangeMapListPage(string id, int page, string direction)
    {
        page += (direction == "previous" ? -1 : 1);

        var embed = await _SteamAPIService.GetLatestMapsEmbed(page: page);

        int maxItemPage = 4; // We know this because latest map query always returns 5 maps. And 1 map per page starting from 0.

        var component = new ComponentBuilder()
                .WithButton("Previous", $"maplist:{Context.Interaction.User.Id},{page},previous", disabled: (page == 0 ? true : false))
                .WithButton("Next", $"maplist:{Context.Interaction.User.Id},{page},next", disabled: (page >= maxItemPage ? true : false))
                .Build();

        var interaction = Context.Interaction as IComponentInteraction;
        await interaction.UpdateAsync(a => { a.Embed = embed; a.Components = component; a.Content = $"__**Latest Maps: ({page + 1}/5)**__"; });
    }

    // Use [ComponentInteraction] to handle message component interactions. Message component interaction with the matching customId will be executed.
    // Alternatively, you can create a wild card pattern using the '*' character. Interaction Service will perform a lazy regex search and capture the matching strings.
    // You can then access these capture groups from the method parameters, in the order they were captured. Using the wild card pattern, you can cherry pick component interactions.
    //[ComponentInteraction("musicSelect:*,*")]
    //public async Task ButtonPress(string id, string name)
    //{
    //    await RespondAsync($"Playing song: {name}/{id}");
    //}

    // Select Menu interactions, contain ids of the menu options that were selected by the user. You can access the option ids from the method parameters.
    // You can also use the wild card pattern with Select Menus, in that case, the wild card captures will be passed on to the method first, followed by the option ids.
    //[ComponentInteraction("roleSelect")]
    //public async Task RoleSelect(string[] selections)
    //{
    //    //throw new NotImplementedException();
    //}

    // This command will greet target user in the channel this was executed in.
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [UserCommand("greet")]
    public async Task GreetUserAsync(IUser user)
        => await RespondAsync(text: $":wave: {Context.User} said hi to you, <@{user.Id}>!");

    // Pins a message in the channel it is in.
    [RequireUserPermission(GuildPermission.ManageChannels)]
    [RateLimit(4)]
    [MessageCommand("pin")]
    public async Task PinMessageAsync(IMessage message)
    {
        // make a safety cast to check if the message is ISystem- or IUserMessage
        if (message is not IUserMessage userMessage)
            await RespondAsync(text: ":x: You cant pin system messages!");

        // if the pins in this channel are equal to or above 50, no more messages can be pinned.
        else if ((await Context.Channel.GetPinnedMessagesAsync()).Count >= 50)
            await RespondAsync(text: ":x: You cant pin any more messages, the max has already been reached in this channel!");

        else
        {
            await userMessage.PinAsync();
            await RespondAsync(":white_check_mark: Successfully pinned message!");
        }
    }

    //[RequireUserPermission(GuildPermission.BanMembers)]
    //[SlashCommand("setchannelstest", "Sets Channels for notifications")]
    //public async Task SetChannelModal()
    //{
    //    var modal = new ModalBuilder()
    //        .WithTitle("Set Channels for Miner Bot")
    //        .AddTextInput(new TextInputBuilder() 
    //        { 
    //            Label = "New Armor Style Notifications",
    //            Placeholder = "Bruh",
    //            CustomId = "ArmorChannel"
    //        })
    //        .WithCustomId("setchannels")
    //        .Build();

    //    await RespondWithModalAsync(modal);
    //}


}