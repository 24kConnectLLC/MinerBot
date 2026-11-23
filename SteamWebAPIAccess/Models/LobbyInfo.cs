namespace SteamWebAPIAccess.Models
{
    // Who hosts a lobby: a player, or the game's own Gamenight or Official host
    public enum LobbyHostKind
    {
        Player,
        Gamenight,
        Official
    }

    public class LobbyInfo
    {
        // The host's name as Steam has it, or Gamenight or Official for a lobby of that kind, whatever its host is called.
        // HostKind says which, so a player who is named Gamenight isn't taken for one.
        public string HostName { get; set; }
        public LobbyHostKind HostKind { get; set; }
        public string MapName { get; set; }
        public string LobbyType { get; set; }
        public ulong OwnerID { get; set; }
        public ulong LobbyId { get; set; }
        public float Distance { get; set; }
        public string Modes { get; set; }
        public string JoinURL { get; set; }
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public uint VersionNumber { get; set; }
    }
}
