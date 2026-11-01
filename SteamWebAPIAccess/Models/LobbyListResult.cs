using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamWebAPIAccess.Models
{
    public class LobbyListResult
    {
        // False when Steam gave no answer, which isn't the same as an answer with no lobbies in it
        public bool Available { get; set; }
        public List<LobbyInfo> Lobbies { get; set; } = new();
    }
}
