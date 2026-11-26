namespace DataAccessLibrary.Status.Models
{
    // The open public lobbies at one notification run's read, counts only: never a lobby, a host or a map. The names match
    // the columns, as in Guild. When Steam gave no answer, every count is null, so it never reads as an empty game.
    public class LobbySample
    {
        // The minute's start, UTC, with no seconds
        public DateTime minute { get; set; }

        public bool steam_answered { get; set; }

        // Every listed lobby, of every version, its players and its slots
        public ushort? lobbies { get; set; }
        public ushort? players { get; set; }
        public ushort? slots { get; set; }

        // The lobbies and players of the game's current version
        public ushort? current_lobbies { get; set; }
        public ushort? current_players { get; set; }

        // Lobbies with no free slot, and the game nights' and the official servers' lobbies
        public ushort? full_lobbies { get; set; }
        public ushort? gamenight_lobbies { get; set; }
        public ushort? official_lobbies { get; set; }
    }
}
