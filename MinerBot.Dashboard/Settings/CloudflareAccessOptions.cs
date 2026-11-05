namespace MinerBot.Dashboard.Settings
{
    // The CloudflareAccess section. With both empty, as until the owner sets up Access in Cloudflare, the check is off:
    // nothing is checked and Cloudflare's keys are never fetched. CloudflareAccessOptionsValidator checks both at start-up.
    internal sealed class CloudflareAccessOptions
    {
        public const string Section = "CloudflareAccess";

        // The team's host name alone, such as yourteam.cloudflareaccess.com. It signs the tokens and serves the keys.
        public string TeamDomain { get; set; } = "";

        // The Access application's AUD tag, which every token for that application carries
        public string Audience { get; set; } = "";

        // The validator refuses one without the other, so both are set or neither is
        public bool Enabled => !string.IsNullOrEmpty(TeamDomain) && !string.IsNullOrEmpty(Audience);

        // What a token's iss must be exactly
        public string Issuer => "https://" + TeamDomain;

        // Where the team's signing keys are published
        public Uri CertsAddress => new("https://" + TeamDomain + "/cdn-cgi/access/certs");
    }
}
