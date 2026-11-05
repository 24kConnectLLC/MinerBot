namespace MinerBot.Dashboard.Settings
{
    // The Discord section: the MinerBot Dashboard application, which is only for signing in and isn't the bot's own, so
    // a leaked secret can't touch the bot's slash commands. The endpoints are Discord's unless configured otherwise.
    internal sealed class DiscordSignInOptions
    {
        public const string Section = "Discord";

        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";

        public string AuthorizationEndpoint { get; set; } = "https://discord.com/oauth2/authorize";
        public string TokenEndpoint { get; set; } = "https://discord.com/api/oauth2/token";
        public string RevocationEndpoint { get; set; } = "https://discord.com/api/oauth2/token/revoke";

        // Discord sends unversioned /api requests to its default version, so this one names v10
        public string UserInformationEndpoint { get; set; } = "https://discord.com/api/v10/users/@me";
    }
}
