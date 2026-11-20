using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Requests
{
    // The dashboard's requests to the bot as the bot reads and answers them, and the settings only those requests change, in
    // MinerBot's own database, with the bot's own login. The dashboard adds a request and never changes it. The bot decides
    // one refused or expired without taking it, or takes it with one conditional UPDATE that only one call can win, then
    // writes how it ended. Writes aren't tried again, because the first try may have gone through, so each says in its WHERE
    // when it applies, and returns whether this call matched the row: MySql.Data counts the rows a statement matched.
    // Every time is the bot's own clock, which it passes in, never the server's.
    public class BotRequestData : DbBaseConnection, IBotRequestData
    {
        // The columns of a request, in the table's order, for the bot's reads and the dashboard's
        internal const string RequestColumns = @"id, request_key, kind, actor_id, actor_name, actor_role, guild_id, channel_id, argument, created_at,
                                                 expires_at, signature, state, run_id, claimed_at, ended_at, result, result_number, dm_state";

        internal const string SettingsColumns = @"posts_paused, pause_reason, pause_changed_at, pause_changed_by, pause_request_id, status_text,
                                                  status_changed_at, status_changed_by, status_request_id, updated_at";

        // What bot_commands and bot_settings hold, counted in code points the way MariaDB counts them
        const int ResultLimit = 300;
        const int ReasonLimit = 200;
        const int StatusTextLimit = 128;

        private readonly DatabaseNames _names;

        public BotRequestData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // The IDs of the requests that wait, and this run's that it took but hasn't finished, oldest first. Only the IDs, so a
        // row with a value that can't be read, such as a time of 0000-00-00, can't stop the list: each row is then read on
        // its own, and one that can't be read is decided by its ID. Only restarts when restartOnly is set, so a queue of
        // other requests can't hide one while the bot is off Discord.
        public async Task<IReadOnlyList<ulong>> ListOpenIds(ulong runId, int take, bool restartOnly)
        {
            if (take is < 1 or > MaxPageSize)
                throw new ArgumentOutOfRangeException(nameof(take), take, $"A list of requests takes from 1 to {MaxPageSize}");

            string sql = $@"SELECT id
                            FROM {_names.Bot}.bot_commands
                            WHERE (state IS NULL OR (state = @running AND run_id = @runId)){(restartOnly ? " AND kind = @restart" : "")}
                            ORDER BY id
                            LIMIT @take";

            var ids = await LoadDataEnum<ulong, dynamic>(sql, new { running = RequestStates.Running, runId, restart = RequestKinds.BotRestart, take }).ConfigureAwait(false);
            return ids.ToList();
        }

        public Task<BotRequestRow?> GetRequest(ulong id)
        {
            string sql = $@"SELECT {RequestColumns}
                            FROM {_names.Bot}.bot_commands
                            WHERE id = @id";

            return LoadFirstData<BotRequestRow?, dynamic>(sql, new { id });
        }

        // Refused or expired, without taking it: only a request that still waits, so a claim is never undone. False when
        // another call took or decided it first.
        public async Task<bool> Decide(ulong id, ulong runId, string state, string result, DateTime now)
        {
            if (state is not (RequestStates.Refused or RequestStates.Expired))
                throw new ArgumentException($"A request is decided {RequestStates.Refused} or {RequestStates.Expired}, without being taken", nameof(state));

            RequireResult(result);
            RequireUtc(now, nameof(now));

            string sql = $@"UPDATE {_names.Bot}.bot_commands
                            SET state = @state, run_id = @runId, ended_at = @now, result = @result
                            WHERE id = @id AND state IS NULL";

            return await SaveDataCount(sql, new { id, runId, state, result = Clip(result, ResultLimit), now }).ConfigureAwait(false) == 1;
        }

        // Takes a request that waits and hasn't expired by the bot's clock. Exactly one call can match, so a request is acted
        // on at most once, whatever else reads it. Called only once every check and limit has passed.
        public async Task<bool> Claim(ulong id, ulong runId, DateTime now)
        {
            RequireUtc(now, nameof(now));

            string sql = $@"UPDATE {_names.Bot}.bot_commands
                            SET state = @running, run_id = @runId, claimed_at = @now
                            WHERE id = @id AND state IS NULL AND expires_at > @now";

            return await SaveDataCount(sql, new { id, runId, running = RequestStates.Running, now }).ConfigureAwait(false) == 1;
        }

        // How a request this run took ended. It doesn't look at the state, so a later write of this run's wins, over its own
        // earlier one and over a sweep that marked the request unknown. A request another run took, or one that was decided
        // rather than taken, is left alone.
        public async Task<bool> Finish(ulong id, ulong runId, string state, string result, ulong? number, DateTime now)
        {
            if (state is not (RequestStates.Done or RequestStates.Failed or RequestStates.Unknown))
                throw new ArgumentException($"A request that was taken ends {RequestStates.Done}, {RequestStates.Failed} or {RequestStates.Unknown}", nameof(state));

            RequireResult(result);
            RequireUtc(now, nameof(now));

            string sql = $@"UPDATE {_names.Bot}.bot_commands
                            SET state = @state, ended_at = @now, result = @result, result_number = @number
                            WHERE id = @id AND run_id = @runId AND claimed_at IS NOT NULL";

            return await SaveDataCount(sql, new { id, runId, state, result = Clip(result, ResultLimit), number, now }).ConfigureAwait(false) == 1;
        }

        // Whether the direct message to the owner about a request went. Written once, so a duplicate can't change it.
        public async Task<bool> SetDm(ulong id, string dmState)
        {
            if (!RequestDmStates.IsKnown(dmState))
                throw new ArgumentException($"A direct message's state is one of: {string.Join(", ", RequestDmStates.All)}", nameof(dmState));

            string sql = $@"UPDATE {_names.Bot}.bot_commands
                            SET dm_state = @dmState
                            WHERE id = @id AND dm_state IS NULL";

            return await SaveDataCount(sql, new { id, dmState }).ConfigureAwait(false) == 1;
        }

        // The requests other runs took and never finished, marked unknown, since they may or may not have happened, and
        // returned for their events. Only those taken before this run started, so another process that runs now, such as a
        // developer's bot on the same database, keeps its own. One transaction: the locked read holds exactly the rows the
        // change then marks.
        public Task<IReadOnlyList<SweptRequestRow>> SweepOtherRuns(ulong runId, DateTime startedAt, string result, DateTime now)
        {
            RequireUtc(startedAt, nameof(startedAt));
            RequireUtc(now, nameof(now));
            RequireResult(result);

            const string Unfinished = "state = @running AND run_id <> @runId AND claimed_at < @startedAt";

            // Every statement names the database, here, so a missing BotConnection fails before any connection is tried
            var bot = _names.Bot;

            string read = $@"SELECT id, kind
                             FROM {bot}.bot_commands
                             WHERE {Unfinished}
                             ORDER BY id
                             FOR UPDATE";

            string mark = $@"UPDATE {bot}.bot_commands
                             SET state = @unknown, ended_at = @now, result = @result
                             WHERE {Unfinished}";

            var parameters = new { running = RequestStates.Running, unknown = RequestStates.Unknown, runId, startedAt, now, result = Clip(result, ResultLimit) };

            return InTransaction(async transaction =>
            {
                var swept = await transaction.Query<SweptRequestRow>(read, parameters).ConfigureAwait(false);
                await transaction.Execute(mark, parameters).ConfigureAwait(false);

                return swept;
            });
        }

        // The requests of these kinds that any run took since the time given, and when it took the oldest, for a limit that
        // lasts across restarts. Only taken requests count, since a decision never sets claimed_at, so refused and expired
        // rows can't use up a limit. With a server, only that server's.
        public Task<ClaimCountRow> CountClaims(IReadOnlyCollection<string> kinds, DateTime since, ulong? guildId = null)
        {
            if (kinds == null || kinds.Count == 0)
                throw new ArgumentException("A limit counts at least one kind", nameof(kinds));

            if (kinds.Any(kind => RequestKinds.Find(kind) == null))
                throw new ArgumentException("A limit counts only kinds on the request list", nameof(kinds));

            RequireUtc(since, nameof(since));

            string sql = $@"SELECT COUNT(*) AS count, MIN(claimed_at) AS oldest
                            FROM {_names.Bot}.bot_commands
                            WHERE kind IN @kinds AND claimed_at >= @since{(guildId != null ? " AND guild_id = @guildId" : "")}";

            return LoadFirstData<ClaimCountRow, dynamic>(sql, new { kinds, since, guildId });
        }

        // Restarts that earlier runs took and finished but never told the owner about, because the bot wasn't up on Discord
        // before it stopped, taken since the time given
        public async Task<IReadOnlyList<BotRequestRow>> ListUntoldRestarts(ulong runId, DateTime since)
        {
            RequireUtc(since, nameof(since));

            string sql = $@"SELECT {RequestColumns}
                            FROM {_names.Bot}.bot_commands
                            WHERE kind = @restart AND state = @done AND run_id <> @runId AND dm_state IS NULL AND claimed_at >= @since
                            ORDER BY id";

            var rows = await LoadDataEnum<BotRequestRow, dynamic>(sql, new { restart = RequestKinds.BotRestart, done = RequestStates.Done, runId, since })
                .ConfigureAwait(false);
            return rows.ToList();
        }

        // Null when no request has changed them yet, which means posts aren't paused and the status is the default
        public Task<BotSettingsRow?> GetSettings()
        {
            string sql = $@"SELECT {SettingsColumns}
                            FROM {_names.Bot}.bot_settings
                            WHERE id = 1";

            return LoadFirstData<BotSettingsRow?, object?>(sql, null);
        }

        // Pauses posts with the reason given, or resumes them. Only the pause's own columns change, so the status text stays.
        public Task SavePause(bool paused, string? reason, DateTime changedAt, ulong changedBy, ulong requestId, DateTime now)
        {
            RequireUtc(changedAt, nameof(changedAt));
            RequireUtc(now, nameof(now));

            if (paused && (string.IsNullOrEmpty(reason) || CodePoints(reason) > ReasonLimit))
                throw new ArgumentException($"A pause has a reason of 1 to {ReasonLimit} characters", nameof(reason));

            if (!paused && reason != null)
                throw new ArgumentException("Posts that aren't paused have no reason", nameof(reason));

            string sql = $@"INSERT INTO {_names.Bot}.bot_settings (id, posts_paused, pause_reason, pause_changed_at, pause_changed_by, pause_request_id, updated_at)
                            VALUES (1, @paused, @reason, @changedAt, @changedBy, @requestId, @now)
                            ON DUPLICATE KEY UPDATE posts_paused = VALUES(posts_paused), pause_reason = VALUES(pause_reason),
                            pause_changed_at = VALUES(pause_changed_at), pause_changed_by = VALUES(pause_changed_by),
                            pause_request_id = VALUES(pause_request_id), updated_at = VALUES(updated_at)";

            return SaveDataCount(sql, new { paused, reason, changedAt, changedBy, requestId, now });
        }

        // Sets the status text, or null for the default. Only the status's own columns change, so a pause stays, and the
        // first row it makes isn't paused.
        public Task SaveStatus(string? text, DateTime changedAt, ulong changedBy, ulong requestId, DateTime now)
        {
            RequireUtc(changedAt, nameof(changedAt));
            RequireUtc(now, nameof(now));

            if (text != null && (text.Length == 0 || CodePoints(text) > StatusTextLimit))
                throw new ArgumentException($"A status text has 1 to {StatusTextLimit} characters, or none for the default", nameof(text));

            string sql = $@"INSERT INTO {_names.Bot}.bot_settings (id, posts_paused, status_text, status_changed_at, status_changed_by, status_request_id, updated_at)
                            VALUES (1, 0, @text, @changedAt, @changedBy, @requestId, @now)
                            ON DUPLICATE KEY UPDATE status_text = VALUES(status_text), status_changed_at = VALUES(status_changed_at),
                            status_changed_by = VALUES(status_changed_by), status_request_id = VALUES(status_request_id),
                            updated_at = VALUES(updated_at)";

            return SaveDataCount(sql, new { text, changedAt, changedBy, requestId, now });
        }

        // A result is the bot's own words, cut to the column as it's written
        static void RequireResult(string result)
        {
            if (string.IsNullOrEmpty(result))
                throw new ArgumentException("A request's result says what happened, so it can't be empty", nameof(result));
        }
    }
}
