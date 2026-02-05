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

        void AuthenticateClient();
        void SetCredentials(string key, string shouldRemember, string username = "", string password = "");
        void UpdateHandlerCallback();
    }
}