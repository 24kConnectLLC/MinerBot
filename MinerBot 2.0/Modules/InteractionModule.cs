using Discord;
using Discord.Interactions;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Handlers;
using MinerBot_2._0.Services;

namespace MinerBot_2._0.Modules;

// Interaction modules must be public and inherit from an IInteractionModuleBase
public class InteractionModule : InteractionModuleBase<SocketInteractionContext>
{
    // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
    // Dependency Injection will fill this value in for us
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
    [SlashCommand("help", "Lists all of Miner Bot's slash commands.")]
    public async Task Help()
    {
        await RespondAsync(embed: DiscordService.GetHelpEmbed());
    }

    [RateLimit(3)]
    [Group("murder-miners", "Commands for Murder Miners")]

    public class MurderMinersGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }

        [SlashCommand("lobbies", "Finds all Lobbies currently publically open in Murder Miners.")]
        public async Task GetLobbyList()
        {
            await DeferAsync();

            await FollowupAsync(embed: await _SteamAPIService.LobbyListEmbed());
        }
    }

    [RateLimit(5)]
    [Group("armor", "Commands for Armor Styles")]
    public class ArmorGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public ArmorService _ArmorService { get; set; }

        [SlashCommand("list", "Gets a list of all armor styles.")]
        public async Task ArmorList([Summary(description: "Optional: Filter armor by helmet by adding the helmet name.")] [Autocomplete(typeof(HelmetAutocompleteHandler))] string helmetType = "All")
        {
            if (!ArmorService.TryParseHelmet(helmetType, out var helmet))
            {
                await RespondAsync("hmmm.. that helmet is not in my helmet list. Please use /armor helmets to see the full helmet list.", ephemeral: true, allowedMentions: AllowedMentions.None);
                return;
            }

            await DeferAsync();

            var armors = await _ArmorService.GetArmorListNames(helmet);

            var itemsInPage = armors.Count();

            if (itemsInPage > _ArmorService.ItemsPerPage)
            {
                var component = new ComponentBuilder()
                .WithButton("Previous", $"armorlist:{Context.Interaction.User.Id},0,previous,{helmet}", disabled: true)
                .WithButton("Next", $"armorlist:{Context.Interaction.User.Id},0,next,{helmet}")
                .Build();

                await FollowupAsync(embed: await _ArmorService.ArmorListEmbed(helmet), components: component);
            }
            else
                await FollowupAsync(embed: await _ArmorService.ArmorListEmbed(helmet));
        }

        [SlashCommand("lookup", "Finds the armor style you are looking for. Use /armor list to find all armor styles.")]
        public async Task GetArmor([Summary(description: "Required: Name of armor style.")] [Autocomplete(typeof(ArmorAutocompleteHandler))] string armorName)
        {
            await DeferAsync();

            var armor = await _ArmorService.GetArmorStyle(armorName);
            if (armor == null)
            {
                await FollowupAsync(embed: await _ArmorService.ErrorEmbed());
                return;
            }

            var result = await _ArmorService.ArmorEmbed(armor);

            if (result.ArmorImage is FileAttachment image)
            {
                // The attachment holds the image, and is closed even when the followup fails
                using (image)
                    await FollowupWithFileAsync(image, embed: result.ArmorEmbed);
            }
            else
                await FollowupAsync(embed: result.ArmorEmbed);
        }

        [SlashCommand("helmets", "Gets a list of all possible helmet types.")]
        public async Task GetHelmets()
        {
            await RespondAsync(embed: await _ArmorService.GetHelmetsEmbed());
        }
    }

    [Group("maps", "Commands for Steam Workshop Maps.")]
    public class WorkshopGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }

        [RateLimit(5)]
        [SlashCommand("latest", "Returns up to 5 detailed results of the newest Maps on the Murder Miners Workshop.")]
        public async Task GetLatestWorkshopItems()
        {
            await DeferAsync();

            // One read for the embed and the buttons, which can be fewer than five maps
            var latestMaps = await _SteamAPIService.GetLatestWorkshopItems();

            // GetData returns an empty list when Steam fails
            if (latestMaps.Count == 0)
            {
                await FollowupAsync(embed: SteamAPIService.SteamUnreachableEmbed());
                return;
            }

            var component = new ComponentBuilder()
                .WithButton("Previous", $"maplist:{Context.Interaction.User.Id},0,previous", disabled: true)
                .WithButton("Next", $"maplist:{Context.Interaction.User.Id},0,next", disabled: latestMaps.Count == 1)
                .Build();

            await FollowupAsync($"__**Latest Maps: (1/{latestMaps.Count})**__", embed: await _SteamAPIService.GetLatestMapsEmbed(latestMaps),
                components: component);
        }

        [RateLimit(3)]
        [SlashCommand("lookup", "Returns a detailed result of the Murder Miners Workshop Map Link you provided.")]
        public async Task GetWorkshopItem([Summary(description: "Required: A Steam Workshop Link or ID of the Map.")] string mapURL)
        {
            await DeferAsync();

            await FollowupAsync(embed: await _SteamAPIService.GetMapEmbed(mapURL));
        }
    }

    [RateLimit(3)]
    [Group("more", "Other Useful Commands.")]
    public class MoreGroupModule() : InteractionModuleBase<SocketInteractionContext>
    {
        public SteamAPIService _SteamAPIService { get; set; }
        public DiscordService _DiscordService { get; set; }

        [SlashCommand("steam_id", "Returns all Steam ID formats for the supplied steam profile.")]
        public async Task GetSteamID([Summary(description: "Required: A USER Steam Profile Link.")] string profileURL)
        {
            await DeferAsync();

            await FollowupAsync(embed: await _SteamAPIService.SteamIDEmbed(profileURL));
        }

        [SlashCommand("info", "More Info and Credits about Miner Bot")]
        public async Task Info()
        {
            await RespondAsync(embed: await _DiscordService.GetInfoEmbed());
        }
    }

    // With the Attribute DoUserCheck you can make sure that only the user this button targets can click it. This is defined by the first wildcard: *.
    // See Attributes/DoUserCheckAttribute.cs for elaboration.

    [DoUserCheck]
    [ComponentInteraction("armorlist:*,*,*,*", ignoreGroupNames: true, TreatAsRegex = false)]
    public async Task ChangeArmorListPage(string id, int page, string direction, string helmetType)
    {
        await DeferAsync();

        page += (direction == "previous" ? -1 : 1);

        var helmetTypeE = Enum.Parse<HelmentType>(helmetType);

        var allArmors = (await _ArmorService.GetArmorListNames(helmetTypeE)).ToList();

        // A page past the end shows the last one, because armor can be removed after the buttons were made
        var lastPage = Math.Max(0, (allArmors.Count - 1) / _ArmorService.ItemsPerPage);
        page = Math.Clamp(page, 0, lastPage);

        var armors = allArmors.Skip(page * _ArmorService.ItemsPerPage).Take(_ArmorService.ItemsPerPage);

        var embed = await _ArmorService.ArmorListEmbed(helmetTypeE, armors, page);

        var component = new ComponentBuilder()
                .WithButton("Previous", $"armorlist:{Context.Interaction.User.Id},{page},previous,{helmetTypeE}", disabled: (page == 0 ? true : false))
                .WithButton("Next", $"armorlist:{Context.Interaction.User.Id},{page},next,{helmetTypeE}", disabled: (page == lastPage ? true : false))
                .Build();

        await ModifyOriginalResponseAsync(a => { a.Embed = embed; a.Components = component; });
    }

    // A click can ask Steam whenever the cache is empty, so the clicks have a cooldown like the commands. It's checked after
    // DoUserCheck, so a click on someone else's buttons doesn't start one.
    [DoUserCheck]
    [RateLimit(2)]
    [ComponentInteraction("maplist:*,*,*", ignoreGroupNames: true, TreatAsRegex = false)]
    public async Task ChangeMapListPage(string id, int page, string direction)
    {
        await DeferAsync();

        page += (direction == "previous" ? -1 : 1);

        var latestMaps = await _SteamAPIService.GetLatestWorkshopItems();

        // The message keeps its page, and only the person who clicked is told why
        if (latestMaps.Count == 0)
        {
            await FollowupAsync(SteamAPIService.SteamUnreachableText, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        // A page past the end shows the last one, because the list can be shorter than when the buttons were made. One map per page.
        var lastPage = latestMaps.Count - 1;
        page = Math.Clamp(page, 0, lastPage);

        var embed = await _SteamAPIService.GetLatestMapsEmbed(latestMaps, page);

        var component = new ComponentBuilder()
                .WithButton("Previous", $"maplist:{Context.Interaction.User.Id},{page},previous", disabled: (page == 0 ? true : false))
                .WithButton("Next", $"maplist:{Context.Interaction.User.Id},{page},next", disabled: (page == lastPage ? true : false))
                .Build();

        await ModifyOriginalResponseAsync(a => { a.Embed = embed; a.Components = component; a.Content = $"__**Latest Maps: ({page + 1}/{latestMaps.Count})**__"; });
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
    [CommandContextType(InteractionContextType.Guild)]
    [RequireUserPermission(GuildPermission.ModerateMembers)]
    [UserCommand("greet")]
    public async Task GreetUserAsync(IUser user)
        => await RespondAsync(text: $":wave: {Context.User} said hi to you, <@{user.Id}>!");

    // Pins a message in the channel it is in. The bot pins with its own permission, so the member needs Pin Messages in that channel.
    // Discord is asked to offer it in servers only, and a DM that comes in anyway is refused first, as there's no such permission to check there.
    [CommandContextType(InteractionContextType.Guild)]
    [RequireContext(ContextType.Guild)]
    [RequireUserPermission(ChannelPermission.PinMessages)]
    [RateLimit(4)]
    [MessageCommand("pin")]
    public async Task PinMessageAsync(IMessage message)
    {
        await DeferAsync();

        // make a safety cast to check if the message is ISystem- or IUserMessage
        if (message is not IUserMessage userMessage)
            await FollowupAsync(text: ":x: You can't pin system messages!");

        // if the pins in this channel are equal to or above 50, no more messages can be pinned.
        else if ((await Context.Channel.GetPinnedMessagesAsync()).Count >= 50)
            await FollowupAsync(text: ":x: You can't pin any more messages, the max has already been reached in this channel!");

        else
        {
            await userMessage.PinAsync();
            await FollowupAsync(":white_check_mark: Successfully pinned message!");
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

// Top level, because Discord.Net drops the default member permissions of a group nested in a module that has none.
// A server can still let other roles use /admin, so each command checks for Administrator too.
[RateLimit(5)]
[RequireContext(ContextType.Guild)]
[DefaultMemberPermissions(GuildPermission.Administrator)]
[CommandContextType(InteractionContextType.Guild)]
[Group("admin", "Admin Only Commands.")]
public class AdminGroupModule() : InteractionModuleBase<SocketInteractionContext>
{
    public SteamAPIService _SteamAPIService { get; set; }
    public DiscordService _DiscordService { get; set; }

    // A channel option can't be set to none, so leaving one out is how a kind is turned off
    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("set_channels", "Sets where I post each kind of notification. Any kind you leave out is turned off. Admin Only.")]
    public async Task SetNotificationChannel(
        [Summary(description: "Channel for new armor styles. Leave out to turn these off.")] [ChannelTypes(ChannelType.Text, ChannelType.News)] IMessageChannel? armorChannel = null,
        [Summary(description: "Channel for new and updated workshop maps. Leave out to turn these off.")] [ChannelTypes(ChannelType.Text, ChannelType.News)] IMessageChannel? mapChannel = null,
        [Summary(description: "Channel for newly opened public lobbies. Leave out to turn these off.")] [ChannelTypes(ChannelType.Text, ChannelType.News)] IMessageChannel? lobbyChannel = null,
        [Summary(description: "Channel for /admin announce. Leave out to turn announcements off.")] [ChannelTypes(ChannelType.Text, ChannelType.News)] IMessageChannel? announcementsChannel = null)
    {
        // Private from the start, because after a public defer Discord keeps the first followup public
        await DeferAsync(ephemeral: true);

        // Checked before anything is saved, so a server isn't left with a channel the job can't post in
        var bot = Context.Guild.CurrentUser ?? throw new InvalidOperationException("The bot's own member isn't in the guild cache");

        var refused = new[] { armorChannel, mapChannel, lobbyChannel, announcementsChannel }
            .OfType<IGuildChannel>()
            .DistinctBy(channel => channel.Id)
            .Select(channel => (channel.Id, Missing: DiscordService.MissingPermissions(bot, channel, ChannelPermission.ViewChannel, ChannelPermission.SendMessages, ChannelPermission.EmbedLinks)))
            .Where(check => check.Missing.Count > 0)
            .Select(check => $"- {MentionUtils.MentionChannel(check.Id)}: {string.Join(", ", check.Missing)}")
            .ToList();

        if (refused.Count > 0)
        {
            await FollowupAsync($"Nothing was saved, because I'm missing permissions in these channels:\n{string.Join("\n", refused)}\nGive me these permissions there (or pick other channels), then run /admin set_channels again.", ephemeral: true);
            return;
        }

        var guild = new DataAccessLibrary.Discord.Models.Guild()
        {
            guild_id = Context.Guild.Id,
            armor_channel_id = armorChannel?.Id ?? DataAccessLibrary.Discord.Models.Guild.ChannelOff,
            map_channel_id = mapChannel?.Id ?? DataAccessLibrary.Discord.Models.Guild.ChannelOff,
            lobby_channel_id = lobbyChannel?.Id ?? DataAccessLibrary.Discord.Models.Guild.ChannelOff,
            announcements_channel_id = announcementsChannel?.Id ?? DataAccessLibrary.Discord.Models.Guild.ChannelOff
        };

        try
        {
            await _DiscordService.SaveGuild(guild);
        }
        // A save the database didn't answer in time can still go through later, so this asks for a wait, not a retry
        catch (TimeoutException e)
        {
            Console.WriteLine($"[Interaction/Warning] set_channels timed out for {Context.User}: {e.Message}");
            await FollowupAsync("The database didn't answer in time, so the channels may or may not be saved. Please wait a few minutes before setting them again.", ephemeral: true);
            return;
        }

        await FollowupAsync($"Saved. This is where I'll post in this server:\n{DiscordService.ChannelList(guild)}\nAny kind you leave out of /admin set_channels is turned off. See these again with /admin show_channels.", ephemeral: true);
    }

    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("show_channels", "Shows where I post each kind of notification. Admin Only.")]
    public async Task ShowNotificationChannels()
    {
        await DeferAsync(ephemeral: true);

        var guild = await _DiscordService.GetGuild(Context.Guild.Id);

        await FollowupAsync($"This is where I post in this server:\n{DiscordService.ChannelList(guild)}\nChange these with /admin set_channels. Any kind you leave out there is turned off.", ephemeral: true);
    }

    [RequireUserPermission(GuildPermission.Administrator)]
    [SlashCommand("announce", "Creates an announcement. Admin Only.")]
    public async Task Announce([Summary(description: "The message to post, up to 2,000 characters.")] [MaxLength(DiscordConfig.MaxMessageSize)] string message)
    {
        await DeferAsync(ephemeral: true);

        await FollowupAsync(await _DiscordService.SendAnnouncement(message, Context.Guild), ephemeral: true);
    }
}