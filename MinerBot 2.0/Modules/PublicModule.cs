using Discord;
using Discord.Commands;
using MinerBot_2._0.Services;
using MinerBot_2._0.Builders;

namespace MinerBot_2._0.Modules;

// Modules must be public and inherit from an IModuleBase
public class PublicModule : ModuleBase<SocketCommandContext>
{
    // Dependency Injection will fill this value in for us
    public PictureService _PictureService { get; set; }

    public AnnounceService _AnnounceService { get; set; }

    public SteamAPIService _SteamAPIService { get; set; }

    public ArmorService _ArmorService { get; set; }

    [Command("Lobbies")]
    [Alias("LobbyList", "Games")]
    public async Task GetLobbyList()
    {
        await ReplyAsync(embed: await _SteamAPIService.LobbyListEmbed());
    }

    //[Command("CreateLobby")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task CreateLobby()
    //{
    //    await ReplyAsync("Lobby has been created! " + await _SteamAPIService.CreateLobby());
    //}

    //[Command("FindUsers")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task FindUsers([Remainder] string lobbyID)
    //{
    //    await ReplyAsync(await _SteamAPIService.GetUsersInLobby(ulong.Parse(lobbyID)));
    //}

    //[Command("KickUser")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task KickUser(params string[] objects)
    //{
    //    if (objects.Length < 2)
    //        await ReplyAsync("Sorry! Please input the userID and the lobbyID!");
    //    await ReplyAsync(await _SteamAPIService.KickUserFromLobby(ulong.Parse(objects[0]), ulong.Parse(objects[1])));
    //}

    //[Command("SendMessage")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task SendMessageToLobby(params string[] objects)
    //{
    //    if (objects.Length < 2)
    //        await ReplyAsync("Sorry! Please input the userID and the lobbyID!");
    //    await ReplyAsync(await _SteamAPIService.SendMessageToLobby(ulong.Parse(objects[0]), objects[1]));
    //}

    //[Command("InviteUser")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task InviteUserToLobby(params string[] objects)
    //{
    //    if (objects.Length < 2)
    //        await ReplyAsync("Sorry! Please input the userID and the lobbyID!");
    //    await ReplyAsync(await _SteamAPIService.InviteUserToLobby(ulong.Parse(objects[0]), ulong.Parse(objects[1])));
    //}

    //[Command("JoinLobby")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task JoinLobby([Remainder] string lobbyID)
    //{
    //    await ReplyAsync(await _SteamAPIService.JoinLobby(ulong.Parse(lobbyID)));
    //}

    //[Command("LeaveLobby")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //public async Task LeaveLobby([Remainder] string lobbyID)
    //{
    //    await ReplyAsync(await _SteamAPIService.LeaveLobby(ulong.Parse(lobbyID)));
    //}

    [Command("ArmorList")]
    [Alias("ArmourList", "Armor")]
    public async Task ArmorList()
    {
        var component = new ComponentBuilder()
            .WithButton("Previous", "previous")
            .WithButton("Next", "next")
            .Build();

        await ReplyAsync(embed: await _ArmorService.ArmorListEmbed(HelmentType.All), components: component);
    }

    [Command("ArmorList")]
    [Alias("ArmourList")]
    public async Task ArmorListByHelmet([Remainder] string helmetType)
    {
        var component = new ComponentBuilder()
            .WithButton("Previous", "previous")
            .WithButton("Next", "next")
            .Build();

        await ReplyAsync(embed: await _ArmorService.ArmorListEmbed(Enum.Parse<HelmentType>(helmetType.Replace("-","_"),true)), components: component);
    }

    [Command("Armor")]
    [Alias("Armour")]
    public async Task GetArmor([Remainder] string armorName)
    {
        var armor = _ArmorService.GetArmorStyle(armorName).Result;

        if (armor == null)
        {
            await ReplyAsync("hmmm.. that armor is not in my armor list. Please type +armorlist to see the full armor list.");
            return;
        }

        var result = await _ArmorService.ArmorEmbed(armor);

        await Context.Channel.SendFileAsync(result.ArmorImage, embed: result.ArmorEmbed);
    }

    [Command("helmets")]
    public async Task GetHelmets()
    {
        await ReplyAsync(embed: await _ArmorService.GetHelmetsEmbed());
    }

    [Command("Steamid")]
    public async Task GetSteamID([Remainder] string profileURL)
    {
        await ReplyAsync(embed: await _SteamAPIService.SteamIDEmbed(profileURL));
    }

    [Command("newmaps")]
    public async Task GetLatestMaps()
    {
        await ReplyAsync(message:"__**Latest Maps:**__",embeds: await _SteamAPIService.GetLatestMapsEmbeds(totalMaps: 3));
    }

    [Command("Map")]
    public async Task GetMap([Remainder] string workshopID) 
    {
        await ReplyAsync(embed: await _SteamAPIService.GetMapEmbed(workshopID));
    }

    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //[Command("Announce")]
    //public async Task Announce([Remainder] string message)
    //{
    //    await AnnounceService.Announce("Announcement", message);
    //    await ReplyAsync("Announcement Sent!");
    //}

    //// Get info on a user, or the user who invoked the command if one is not specified
    //[Command("userinfo")]
    //public async Task UserInfoAsync(IUser user = null)
    //{
    //    user ??= Context.User;

    //    await ReplyAsync(user.ToString());
    //}

    // Need to make this based off of command description. But slash commands is good enough.
    [Command("help")]
    public async Task GetHelp()
    {
        var embed = new EmbedBuilderTemplate()
            .WithTitle($"Help:")
            .WithDescription("If you need assistance with the bot or have a suggestion, please [Contact Us](https://murderminershub.com/contact-us/)")
            .WithFields([
                new EmbedFieldBuilder {
                    Name = "__Bot Commands:__",
                    Value = 
                         "**+ArmorList**: Gets a list of all armor styles.\n__Optional__: Filter armor by helmet by adding the helmet name. Use +helmets to get all helmet names.\n\n" +
                         "**+Armor**: Finds the armor style you are looking for.\n__Required__: Name of armor style. Use +ArmorList to find all armor styles.\n\n" +
                         "**+Helmets**: Gets a list of all possible helmet types.\n\n" +
                         "**+SteamID**: Returns all Steam ID formats for the supplied steam profile\n__Required__: A Steam Profile Link.\n\n" +
                         "**+Lobbies**: Finds all Lobbies currently publically open in Murder Miners.\n\n" +
                         "**+NewMaps**: Returns the 3-5 detailed results of the most Newst Maps on the Murder Miners Workshop.\n\n" +
                         "**+Map**: Returns a detailed result of the Murder Miners Workshop Map Link you provided.\n__Required__: A Steam Workshop Link of the Map.\n\n"
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
        await ReplyAsync(embed: embed);
    }

    //[Command("ping")]
    //[Alias("pong", "hello")]
    ////[SlashCommand("Ping", "To Ping a Pong")]
    //public Task PingAsync()
    //    => ReplyAsync("pong!");

    //[Command("cat")]
    //public async Task CatAsync()
    //{
    //    // Get a stream containing an image of a cat
    //    var stream = await PictureService.GetCatPictureAsync();
    //    // Streams must be seeked to their beginning before being uploaded!
    //    stream.Seek(0, SeekOrigin.Begin);
    //    await Context.Channel.SendFileAsync(stream, "cat.png");
    //}

    //// Ban a user
    //[Command("ban")]
    //[RequireContext(ContextType.Guild)]
    //// make sure the user invoking the command can ban
    //[RequireUserPermission(GuildPermission.BanMembers)]
    //// make sure the bot itself can ban
    //[RequireBotPermission(GuildPermission.BanMembers)]
    //public async Task BanUserAsync(IGuildUser user, [Remainder] string reason = null)
    //{
    //    await user.Guild.AddBanAsync(user, reason: reason);
    //    await ReplyAsync("ok!");
    //}

    //// [Remainder] takes the rest of the command's arguments as one argument, rather than splitting every space
    //[Command("echo")]
    //public Task EchoAsync([Remainder] string text)
    //// Insert a ZWSP before the text to prevent triggering other bots!
    //    => ReplyAsync('\u200B' + text);

    //// 'params' will parse space-separated elements into a list
    //[Command("list")]
    //public Task ListAsync(params string[] objects)
    //    => ReplyAsync("You listed: " + string.Join("; ", objects));

    //// Setting a custom ErrorMessage property will help clarify the precondition error
    //[Command("guild_only")]
    //[RequireContext(ContextType.Guild, ErrorMessage = "Sorry, this command must be ran from within a server, not a DM!")]
    //public Task GuildOnlyCommand()
    //    => ReplyAsync("Nothing to see here!");
}