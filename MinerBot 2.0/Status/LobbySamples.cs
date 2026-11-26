using DataAccessLibrary.Status.Models;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Status
{
    // A notification run's read of the open lobbies as the sample the analytics count from: how many lobbies Steam listed,
    // of every version, with their players and slots, those of the game's current version, the full ones and the game's own
    // hosts', and never a lobby, a host, a map or a link. With no answer from Steam every count is null, so an outage never
    // reads as an empty game. Keyed by the run's minute, UTC.
    internal static class LobbySamples
    {
        public static LobbySample Of(DateTime at, IReadOnlyCollection<LobbyInfo>? lobbies)
        {
            var minute = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

            if (lobbies == null)
                return new LobbySample { minute = minute, steam_answered = false };

            var current = lobbies.Where(lobby => lobby.VersionNumber == GameVersions.Current).ToList();

            return new LobbySample
            {
                minute = minute,
                steam_answered = true,
                lobbies = Count(lobbies.Count),
                players = Count(lobbies.Sum(lobby => Players(lobby.CurrentPlayers))),
                slots = Count(lobbies.Sum(lobby => Players(lobby.MaxPlayers))),
                current_lobbies = Count(current.Count),
                current_players = Count(current.Sum(lobby => Players(lobby.CurrentPlayers))),
                full_lobbies = Count(lobbies.Count(lobby => lobby.MaxPlayers > 0 && lobby.CurrentPlayers >= lobby.MaxPlayers)),
                gamenight_lobbies = Count(lobbies.Count(lobby => lobby.HostKind == LobbyHostKind.Gamenight)),
                official_lobbies = Count(lobbies.Count(lobby => lobby.HostKind == LobbyHostKind.Official))
            };
        }

        // Steam's numbers as a lobby's metadata gives them, none below 0
        static long Players(int count) => Math.Max(0, count);

        // What the table's columns hold at most, 65,535
        static ushort Count(long count) => (ushort)Math.Clamp(count, 0, ushort.MaxValue);
    }
}
