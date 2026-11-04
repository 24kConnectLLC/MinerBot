namespace DataAccessLibrary.Discord.Models
{
    public class Guild
    {
        // A channel ID of 0 means the server turned that kind of notification off
        public const ulong ChannelOff = 0;

        public ulong guild_id { get; set; }
        public ulong armor_channel_id { get; set; }
        public ulong map_channel_id { get; set; }
        public ulong lobby_channel_id { get; set; }
        public ulong announcements_channel_id { get; set; }
    }
}
