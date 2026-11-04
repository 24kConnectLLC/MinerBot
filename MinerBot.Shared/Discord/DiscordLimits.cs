namespace MinerBot.Shared.Discord
{
    // Discord's limits that the armor rules check. They count UTF-16 characters, the way Discord.Net checks .Length,
    // so an emoji outside the Basic Multilingual Plane counts twice.
    public static class DiscordLimits
    {
        // An autocomplete suggestion's name, which /armor lookup's suggestions are
        public const int AutocompleteChoiceName = 100;

        public const int EmbedTitle = 256;

        public const int EmbedDescription = 4096;
    }
}
