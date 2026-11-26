using DataAccessLibrary.Posting.Models;
using Discord.WebSocket;
using MinerBot.Shared.Delivery;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MinerBot_2._0.Posting
{
    // How long a server's kind waits before its next try, after failures in a row that were its own: 5 minutes, which is the
    // next run, then 15 minutes, an hour, 3 hours, and 6 hours at most, so a channel that stays broken costs Discord about
    // four requests a day. The wait ends early when anything that could have fixed it changes, which waiting_on holds as a
    // hash: the channel, its type, the server's switches and the bot's View Channel, Send Messages, Embed Links and Read
    // Message History there, from the cache. A refresh of the server's copy and a test message that went end it too.
    public static class Backoff
    {
        static readonly TimeSpan[] Steps = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6)];

        // Runs start a moment after their mark, by more or less each time, so a run up to a minute before the try's time tries
        public static readonly TimeSpan Early = TimeSpan.FromMinutes(1);

        // The wait after the n-th failure in a row
        public static TimeSpan WaitAfter(uint failures) => Steps[Math.Clamp((int)Math.Min(failures, int.MaxValue), 1, Steps.Length) - 1];

        // From the run's start, as the next run's start is compared with it
        public static DateTime NextTry(DateTime runStartedAt, uint failures) => runStartedAt + WaitAfter(failures);

        // Whether the server's kind still waits: its time not come, and nothing changed that could fix it
        public static bool Waits(DeliveryStatusRow? status, DateTime runStartedAt, ulong waitingOn) =>
            status is { next_try_at: { } next } && runStartedAt + Early < next && status.waiting_on == waitingOn;

        // What the wait started from, as one number to compare: the same in every process, since it's kept in MinerBot's
        // database across a restart
        public static ulong WaitingOn(SocketGuild guild, ulong channelId, uint switchesVersion)
        {
            var channel = guild.GetChannel(channelId);
            var permissions = 0;

            if (channel != null && guild.CurrentUser is { } bot)
            {
                var allowed = bot.GetPermissions(channel);

                permissions = (allowed.ViewChannel ? DeliveryReasons.ViewChannel : 0) | (allowed.SendMessages ? DeliveryReasons.SendMessages : 0)
                    | (allowed.EmbedLinks ? DeliveryReasons.EmbedLinks : 0) | (allowed.ReadMessageHistory ? DeliveryReasons.ReadMessageHistory : 0);
            }

            Span<byte> bytes = stackalloc byte[20];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, channelId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], channel == null ? -1 : (int)channel.ChannelType);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], switchesVersion);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], permissions);

            return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(bytes));
        }
    }
}
