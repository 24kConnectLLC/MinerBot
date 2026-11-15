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

        // Steam's side of the bot's status, safe to read from any thread
        SteamStatus Status { get; }

        // Called with each change worth a line in the bot's history: one of SteamEvents' kinds, and a detail in fixed
        // wording that never holds a secret. It must be quick, because it runs on the callback pump.
        Action<string, string?>? RecordEvent { get; set; }

        void AuthenticateClient();
        void SetCredentials(string key, string shouldRemember, string username = "", string password = "");
        Task RunCallbacksAsync(CancellationToken stoppingToken);
        Task RenewTokenAsync();
    }
}