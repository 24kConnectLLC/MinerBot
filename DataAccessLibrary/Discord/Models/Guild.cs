using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccessLibrary.Discord.Models
{
    public class Guild
    {
        public ulong guild_id { get; set; }
        public ulong armor_channel_id { get; set; }
        public ulong map_channel_id { get; set; }
        public ulong lobby_channel_id { get; set; }
        public ulong announcements_channel_id { get; set; }
    }
}
