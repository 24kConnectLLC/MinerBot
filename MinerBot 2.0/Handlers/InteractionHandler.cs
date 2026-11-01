using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualBasic;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace MinerBot_2._0.Handlers;

public class InteractionHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _handler;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;

    public InteractionHandler(DiscordSocketClient client, InteractionService handler, IServiceProvider services, IConfiguration config)
    {
        _client = client;
        _handler = handler;
        _services = services;
        _configuration = config;
    }

    public async Task InitializeAsync()
    {
        // Process when the client is ready, so we can register our commands.
        _client.Ready += ReadyAsync;
        _handler.Log += LogAsync;

        // Add the public modules that inherit InteractionModuleBase<T> to the InteractionService
        await _handler.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        // Process the InteractionCreated payloads to execute Interactions commands
        _client.InteractionCreated += HandleInteraction;

        // Also process the result of the command execution.
        _handler.InteractionExecuted += HandleInteractionExecute;
    }

    private async Task LogAsync(LogMessage log)
        => Console.WriteLine(log);

    private async Task ReadyAsync()
    {
        // Register the commands globally.
        // alternatively you can use _handler.RegisterCommandsToGuildAsync() to register commands to a specific guild.
        await _handler.RegisterCommandsGloballyAsync();
    }

    private async Task HandleInteraction(SocketInteraction interaction)
    {
        try
        {
            // Create an execution context that matches the generic type parameter of your InteractionModuleBase<T> modules.
            var context = new SocketInteractionContext(_client, interaction);

            // Execute the incoming command.
            var result = await _handler.ExecuteCommandAsync(context, _services);

            // Due to async nature of InteractionFramework, the result here may always be success.
            // That's why we also need to handle the InteractionExecuted event.
            if (!result.IsSuccess)
                switch (result.Error)
                {
                    case InteractionCommandError.UnmetPrecondition:
                        // implement
                            await interaction.RespondAsync("Seems that you are not allowed to use that command.");
                        break;
                    default:
                        break;
                }
        }
        catch
        {
            // If Slash Command execution fails it is most likely that the original interaction acknowledgement will persist. It is a good idea to delete the original
            // response, or at least let the user know that something went wrong during the command execution.
            if (interaction.Type is InteractionType.ApplicationCommand)
                await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
        }
    }

    private async Task HandleInteractionExecute(ICommandInfo commandInfo, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess)
            return;

        // A throw from here would fire InteractionExecuted again, so nothing may leave this handler.
        try
        {
            // commandInfo is null when nothing matched the interaction.
            Console.WriteLine($"[Interaction/Warning] {commandInfo?.Name ?? "Unknown command"} failed for {context.User}: {result.Error}: {result.ErrorReason}");

            // Autocomplete can't show a message, the user just sees no suggestions.
            if (context.Interaction is IAutocompleteInteraction)
                return;

            // Once a command has deferred, a followup is the only reply that still works.
            // After a public defer on a slash command, Discord keeps that first followup public.
            if (context.Interaction.HasResponded)
                await context.Interaction.FollowupAsync(GetFailureReply(result), ephemeral: true, allowedMentions: AllowedMentions.None);
            else
                await context.Interaction.RespondAsync(GetFailureReply(result), ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Interaction/Error] Couldn't reply to a failed interaction: {e}");
        }
    }

    private static string GetFailureReply(IResult result)
    {
        switch (result.Error)
        {
            case InteractionCommandError.UnmetPrecondition:
                // Written by the preconditions, and a cooldown needs to show its time.
                return result.ErrorReason;
            case InteractionCommandError.UnknownCommand:
                return "That command or button isn't available any more.";
            case InteractionCommandError.BadArgs:
            case InteractionCommandError.ConvertFailed:
            case InteractionCommandError.ParseFailed:
                return "That input didn't work. Please check it and try again.";
            default:
                return "Something went wrong with that command. Please try again later.";
        }
    }

    //private async Task HandleButtonExecute(SocketMessageComponent interaction)
    //{
    //    var ctx = new SocketInteractionContext<SocketMessageComponent>(_client, interaction);
    //    await _handler.ExecuteCommandAsync(ctx, _services);
    //}
}
