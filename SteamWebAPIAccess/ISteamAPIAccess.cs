using SteamKit2;

namespace SteamWebAPIAccess
{
    public interface ISteamAPIAccess
    {
        uint appID { get; set; }
        CallbackManager manager { get; set; }
        string PublisherWebAPIKey { get; set; }
        SteamClient steamClient { get; set; }
        SteamMatchmaking steamMatchmaking { get; }
        SteamUser steamUser { get; set; }

        // Called with problems only the owner can fix, and with the all clear after one
        Func<string, Task>? AlertOwner { get; set; }

        void AuthenticateClient();
        void SetCredentials(string key, string shouldRemember, string username = "", string password = "");
        Task RunCallbacksAsync(CancellationToken stoppingToken);
        Task RenewTokenAsync();
    }
}