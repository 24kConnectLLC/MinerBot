using DataAccessLibrary.Status;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using System.Globalization;

namespace MinerBot_2._0.Status
{
    // What the status sampler reads of Discord. The state is the gateway's connection, and the latency and the count of
    // servers are there only while it's connected, because Discord.Net keeps their last values through a disconnect. Up is
    // connected after a Ready since the start, which the heartbeat waits for.
    public readonly record struct DiscordReading(string State, bool Up, int? LatencyMs, int? Guilds)
    {
        // What the uptime samples record as up on Discord: the bot gets and answers interactions then, Ready or not
        public bool Connected => State == DiscordState.Connected;
    }

    public interface IDiscordStatus
    {
        DiscordReading Read();
    }

    // Records the bot's connection to Discord's gateway, and tells the status sampler whether the bot is up on it.
    // Discord.Net connects again after a drop with a resume, which raises Connected but never Ready, so up means connected
    // now and Ready at least once since the start. A first session whose connection drops while its servers are still
    // arriving never gets its Ready, nor do its resumes, until Discord makes the bot start a new session: the bot is then
    // connected and recorded so, but not up, because its own Ready handlers, such as the one that registers its commands,
    // never ran, so its health check fails as the ready line in its log did. Attached before the bot logs in, and before
    // the other Ready handlers: Discord.Net skips the Ready handlers after one that throws, and without this one's the bot
    // would never count as up.
    public sealed class DiscordStatusHandler(DiscordSocketClient client, BotRecorder recorder, TimeProvider clock) : IDiscordStatus
    {
        readonly object gate = new();

        // The causes already recorded in this spell offline: each failed attempt to connect raises Disconnected too, up to
        // about once a minute, so each cause is recorded once
        readonly HashSet<string> causes = new(StringComparer.Ordinal);

        bool readyFired, connected;
        DateTime? downSince;

        public void Initialize()
        {
            client.Connected += OnConnected;
            client.Disconnected += OnDisconnected;
            client.Ready += OnReady;
        }

        public DiscordReading Read()
        {
            var state = client.ConnectionState;

            // Discord.Net counts the gateway as connected only once READY or RESUMED came
            if (state == ConnectionState.Connected)
                return new DiscordReading(DiscordState.Connected, Volatile.Read(ref readyFired), client.Latency, client.Guilds.Count);

            return new DiscordReading(state == ConnectionState.Connecting ? DiscordState.Connecting : DiscordState.Disconnected, false, null, null);
        }

        // These run on Discord.Net's own tasks, which wait for them, so they only record and return

        internal Task OnConnected()
        {
            try
            {
                string detail;

                lock (gate)
                {
                    var now = clock.GetUtcNow().UtcDateTime;

                    detail = downSince is { } since
                        ? string.Create(CultureInfo.InvariantCulture, $"after {Seconds(now - since)} s down, since {since:yyyy-MM-dd HH:mm:ss} UTC")
                        : string.Create(CultureInfo.InvariantCulture, $"the first connection of this run, {Seconds(now - recorder.StartedAt)} s after the start");

                    connected = true;
                    downSince = null;
                    causes.Clear();
                }

                recorder.Record(BotEvents.DiscordConnected, detail);
            }
            catch (Exception)
            {
                // Never Discord.Net's problem
            }

            return Task.CompletedTask;
        }

        internal Task OnDisconnected(Exception? error)
        {
            try
            {
                var cause = Cause(error);
                bool record;

                lock (gate)
                {
                    if (connected)
                    {
                        connected = false;
                        downSince = clock.GetUtcNow().UtcDateTime;
                        causes.Clear();
                    }

                    record = causes.Add(cause);
                }

                if (record)
                    recorder.Record(BotEvents.DiscordDisconnected, cause);
            }
            catch (Exception)
            {
            }

            return Task.CompletedTask;
        }

        internal Task OnReady()
        {
            Volatile.Write(ref readyFired, true);
            return Task.CompletedTask;
        }

        // The exception's type, and the close code when Discord closed the connection, which Discord.Net wraps. Never the
        // text, which is Discord's.
        internal static string Cause(Exception? error)
        {
            if (error == null)
                return "no reason given";

            for (var inner = error; inner != null; inner = inner.InnerException)
            {
                if (inner is WebSocketClosedException closed)
                    return string.Create(CultureInfo.InvariantCulture, $"{error.GetType().Name}, close code {closed.CloseCode}");
            }

            return error.GetType().Name;
        }

        static string Seconds(TimeSpan time) => Math.Max(0, time.TotalSeconds).ToString("0", CultureInfo.InvariantCulture);
    }
}
