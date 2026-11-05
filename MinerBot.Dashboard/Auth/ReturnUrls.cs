namespace MinerBot.Dashboard.Auth
{
    // Where a sign-in goes afterwards. Only a path on this site, so nobody can use the sign-in to send someone elsewhere.
    internal static class ReturnUrls
    {
        public const string Default = "/admin";

        const int MaxLength = 512;

        // A path such as /admin, never //host or /\host, which browsers read as another site. Printable ASCII only, without
        // spaces: browsers drop control characters before reading the rest, and Kestrel refuses to put anything else in the
        // Location header, which would fail the redirect after the session had been stored. The dashboard's own return
        // addresses are percent-encoded, so none of them is refused.
        public static bool IsLocal(string? url) =>
            !string.IsNullOrEmpty(url) && url.Length <= MaxLength && url[0] == '/'
            && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            && url.All(c => c is >= '!' and <= '~');

        public static string Pick(string? url) => IsLocal(url) ? url! : Default;
    }
}
