using Discord;
using Discord.Webhook;
using MinerBot_2._0.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Services;
public class AnnounceService
{
    private static DiscordWebhookClient _webhook;

    public async Task InitializeWebhookAsync()
    {
       _webhook = new DiscordWebhookClient(Environment.GetEnvironmentVariable("WEBHOOKCHANNEL"));
        Console.WriteLine("Webhook Attached");
    }
    public static async Task Announce(string title, string description)
    {
        // The webhook url follows the format https://discord.com/api/webhooks/{id}/{token}
        // Because anyone with the webhook URL can use your webhook
        // you should NOT hard code the URL or ID + token into your application.

        var embed = new EmbedBuilderTemplate
        {
            Title = title,
            Description = description
        };

        // Webhooks are able to send multiple embeds per message
        // As such, your embeds must be passed as a collection.
        if (_webhook != null)
            await _webhook.SendMessageAsync(text: "Send a message to this webhook!", embeds: new[] { embed.Build() });
    }
}