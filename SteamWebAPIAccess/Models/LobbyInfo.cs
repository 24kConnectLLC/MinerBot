using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamWebAPIAccess.Models
{
    public class LobbyInfo
    {
        public string HostName { get; set; }
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
