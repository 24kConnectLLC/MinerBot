using Discord;
using Discord.Commands;
using Discord.WebSocket;
using System.Reflection;

namespace MinerBot_2._0.Services;

public class CommandHandlingService
{
    private readonly CommandService _commands;
    private readonly DiscordSocketClient _discord;
    private readonly IServiceProvider _services;

    public CommandHandlingService(IServiceProvider services)
    {
        _commands = services.GetRequiredService<CommandService>();
        _discord = services.GetRequiredService<DiscordSocketClient>();
        _services = services;

        // Hook CommandExecuted to handle post-command-execution logic.
        _commands.CommandExecuted += CommandExecutedAsync;
        // Hook Log, which is where the exceptions of failed commands get recorded.
        _commands.Log += LogAsync;
        // Hook MessageReceived so we can process each message to see
        // if it qualifies as a command.
        _discord.MessageReceived += MessageReceivedAsync;
    }

    public async Task InitializeAsync()
    {
        // Register modules that are public and inherit ModuleBase<T>.
        await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
    }

    public async Task MessageReceivedAsync(SocketMessage rawMessage)
    {
        // Ignore system messages, or messages from other bots
        if (!(rawMessage is SocketUserMessage message))
            return;
        if (message.Source != MessageSource.User)
            return;

        // This value holds the offset where the prefix ends
        var argPos = 0;
        // Perform prefix check. You may want to replace this with for a more traditional command format like !help.
        if (!message.HasCharPrefix('+', ref argPos))
            return;

        // @ping bot for command prefix
        //if (!message.HasMentionPrefix(_discord.CurrentUser, ref argPos))
        //      return;

        var context = new SocketCommandContext(_discord, message);
        // Perform the execution of the command. In this method,
        // the command service will perform precondition and parsing check
        // then execute the command if one is matched.
        await _commands.ExecuteAsync(context, argPos, _services);
        // Note that normally a result will be returned by this format, but here
        // we will handle the result in CommandExecutedAsync,
    }

    public async Task CommandExecutedAsync(Optional<CommandInfo> command, ICommandContext context, IResult result)
    {
        // command is unspecified when there was a search failure (command not found); we don't care about these errors
        if (!command.IsSpecified)
            return;

        // the command was successful, we don't care about this result, unless we want to log that a command succeeded.
        if (result.IsSuccess)
            return;

        // Exceptions were already logged with their stack trace through _commands.Log.
        if (result.Error != CommandError.Exception)
            Console.WriteLine($"[Command/Warning] {command.Value.Name} failed for {context.User}: {result}");

        // the command failed, let's notify the user that something happened.
        // The details stay in the log, only fixed text goes to the channel.
        switch (result.Error)
        {
            case CommandError.BadArgCount:
            case CommandError.ParseFailed:
                await context.Channel.SendMessageAsync("That command needs different input. Type +help to see how to use it.", allowedMentions: AllowedMentions.None);
                break;
            case CommandError.UnmetPrecondition:
                await context.Channel.SendMessageAsync("You can't use that command here.", allowedMentions: AllowedMentions.None);
                break;
            default:
                await context.Channel.SendMessageAsync("Something went wrong with that command. Please try again later.", allowedMentions: AllowedMentions.None);
                break;
        }
    }

    private Task LogAsync(LogMessage log)
    {
        Console.WriteLine(log);
        return Task.CompletedTask;
    }
}