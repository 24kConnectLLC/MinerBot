using Discord.Interactions;

namespace MinerBot.Tests
{
    // Commands that fail on purpose. InteractionHandler finds this module because it looks in the
    // entry assembly, which is this project when the tests run.
    [Group("test", "Commands for the tests")]
    public class TestCommands : InteractionModuleBase<SocketInteractionContext>
    {
        public const string PrivateDetail = "private detail";

        [SlashCommand("fail", "Fails before any reply")]
        public Task Fail() => throw new InvalidOperationException(PrivateDetail);

        [SlashCommand("defer-and-fail", "Fails after deferring")]
        public async Task DeferAndFail()
        {
            await DeferAsync();
            throw new InvalidOperationException(PrivateDetail);
        }

        [SlashCommand("succeed", "Replies")]
        public Task Succeed() => RespondAsync("Done");
    }
}
