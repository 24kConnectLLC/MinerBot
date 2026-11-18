using Discord;
using Discord.WebSocket;

namespace MinerBot_2._0.Status
{
    // Notes the servers whose copy in MinerBot's database may be out of date, for GuildWriter, which takes each one from
    // Discord.Net's cache and writes it when it changed: a server joined, left, changed or back from an outage, any of its
    // channels or roles, or the bot's own member, which Discord sends without the members intent. The handlers only note
    // the server, and for a leave when it was: they do no I/O and never throw, because they run on Discord.Net's own tasks,
    // which wait for them, and Discord.Net skips an event's later handlers after one that throws. So this is attached before
    // the bot logs in, and before the other handlers, such as GuildEventHandler's, whose LeftGuild awaits the website's
    // database.
    public sealed class GuildWatcher(DiscordSocketClient client, TimeProvider clock)
    {
        readonly object gate = new();
        readonly Dictionary<ulong, Note> notes = new();
        long noted;

        // Each Ready, which Discord.Net raises once a new session's servers have arrived, or stopped arriving, with the
        // servers still unavailable then. Resumes don't raise it.
        long readyVersion, readyWritten;
        ulong[] unavailableAtReady = [];

        // The servers just joined whose JoinedGuild handlers still run, such as GuildEventHandler's, which awaits its welcome
        // message: Discord.Net has them whole, and marks them connected, raising GuildAvailable, only once those have returned
        readonly HashSet<ulong> joining = new();

        // A server's version grows with each note, so a server noted again while its write is under way stays noted
        sealed class Note(long order)
        {
            public readonly long Order = order;
            public long Version;
            public DateTime? LeftAt;
        }

        public void Initialize()
        {
            client.Ready += OnReady;
            client.JoinedGuild += OnJoinedGuild;
            client.LeftGuild += OnLeftGuild;
            client.GuildUpdated += OnGuildUpdated;
            client.GuildAvailable += OnGuildAvailable;
            client.GuildUnavailable += OnGuild;
            client.ChannelCreated += OnChannel;
            client.ChannelUpdated += OnChannelUpdated;
            client.ChannelDestroyed += OnChannel;
            client.RoleCreated += OnRole;
            client.RoleUpdated += OnRoleUpdated;
            client.RoleDeleted += OnRole;
            client.GuildMemberUpdated += OnMemberUpdated;
        }

        // These run on Discord.Net's own tasks, which wait for them, so they only note and return

        // The servers Discord listed with the session and never sent whole since are in an outage, and the cache has nothing
        // of them but their IDs. Discord.Net counts one Discord sent with nothing but its ID as connected, so the name it
        // doesn't have is what tells.
        internal Task OnReady() => Noting(() =>
        {
            var unavailable = client.Guilds.Where(guild => guild.Name == null).Select(guild => guild.Id).ToArray();

            lock (gate)
            {
                readyVersion++;
                unavailableAtReady = unavailable;
            }
        });

        internal Task OnGuild(SocketGuild guild) => Noting(() => Changed(guild.Id));

        internal Task OnJoinedGuild(SocketGuild guild) => Noting(() =>
        {
            lock (gate)
            {
                NoteOf(guild.Id).Version++;
                joining.Add(guild.Id);
            }
        });

        internal Task OnGuildAvailable(SocketGuild guild) => Noting(() =>
        {
            lock (gate)
            {
                NoteOf(guild.Id).Version++;
                joining.Remove(guild.Id);
            }
        });

        internal Task OnGuildUpdated(SocketGuild before, SocketGuild after) => OnGuild(after);

        internal Task OnLeftGuild(SocketGuild guild) => Noting(() =>
        {
            var at = clock.GetUtcNow().UtcDateTime;

            lock (gate)
            {
                var note = NoteOf(guild.Id);
                note.Version++;
                note.LeftAt = at;
                joining.Remove(guild.Id);
            }
        });

        // Direct messages have no server
        internal Task OnChannel(SocketChannel channel) => Noting(() =>
        {
            if (channel is SocketGuildChannel inGuild)
                Changed(inGuild.Guild.Id);
        });

        internal Task OnChannelUpdated(SocketChannel before, SocketChannel after) => OnChannel(after);

        internal Task OnRole(SocketRole role) => Noting(() => Changed(role.Guild.Id));

        internal Task OnRoleUpdated(SocketRole before, SocketRole after) => OnRole(after);

        // Only the bot's own, such as when it's given a role
        internal Task OnMemberUpdated(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after) => Noting(() =>
        {
            if (after.Id == client.CurrentUser?.Id)
                Changed(after.Guild.Id);
        });

        // For GuildWriter

        // Every server, such as each one in the cache at a new session and every 15 minutes
        internal void NoteAll(IEnumerable<ulong> guildIds)
        {
            lock (gate)
            {
                foreach (var guildId in guildIds)
                    NoteOf(guildId).Version++;
            }
        }

        // The servers noted, oldest first, each with its version and, when the bot left it, when
        internal List<(ulong GuildId, long Version, DateTime? LeftAt)> PeekNotes()
        {
            lock (gate)
                return notes.OrderBy(note => note.Value.Order).Select(note => (note.Key, note.Value.Version, note.Value.LeftAt)).ToList();
        }

        // A server noted again since its version was taken stays noted
        internal void Written(ulong guildId, long version)
        {
            lock (gate)
            {
                if (notes.TryGetValue(guildId, out var note) && note.Version == version)
                    notes.Remove(guildId);
            }
        }

        // So the leave isn't written again while the rest of the note waits, unless the bot left once more since
        internal void LeaveWritten(ulong guildId, DateTime leftAt)
        {
            lock (gate)
            {
                if (notes.TryGetValue(guildId, out var note) && note.LeftAt == leftAt)
                    note.LeftAt = null;
            }
        }

        // Whether the server was just joined and Discord.Net hasn't marked it connected yet, which isn't an outage
        internal bool Joining(ulong guildId)
        {
            lock (gate)
                return joining.Contains(guildId);
        }

        // The newest session's Ready, until it's been dealt with
        internal (long Version, ulong[] Unavailable)? PeekReady()
        {
            lock (gate)
                return readyVersion > readyWritten ? (readyVersion, unavailableAtReady) : null;
        }

        internal void ReadyWritten(long version)
        {
            lock (gate)
                readyWritten = Math.Max(readyWritten, version);
        }

        void Changed(ulong guildId)
        {
            lock (gate)
                NoteOf(guildId).Version++;
        }

        Note NoteOf(ulong guildId)
        {
            if (!notes.TryGetValue(guildId, out var note))
                notes[guildId] = note = new Note(++noted);

            return note;
        }

        static Task Noting(Action note)
        {
            try
            {
                note();
            }
            catch (Exception)
            {
                // Never Discord.Net's problem
            }

            return Task.CompletedTask;
        }
    }
}
