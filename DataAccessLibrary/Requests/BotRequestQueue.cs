using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Requests
{
    // The dashboard's requests to the bot, in MinerBot's own database, as the dashboard writes and reads them. Its login may
    // add a request only by naming the columns the dashboard fills in, so the bot's columns start NULL, and it can never
    // change or delete one. A request goes in with its audit row, in one transaction on MinerBot's own connection. The
    // pages' reads give every time through Readable and never the key or the signature.
    public class BotRequestQueue : DbBaseConnection, IBotRequestQueue, IBotRequestReads
    {
        // The columns a page reads: every one but the key and the signature, with each time through Readable
        static readonly string PageColumns = $@"c.id, c.kind, c.actor_id, c.actor_name, c.actor_role, c.guild_id, c.channel_id, c.argument,
                                               {Readable("c.created_at")}, {Readable("c.expires_at")}, c.state, c.run_id,
                                               {Readable("c.claimed_at")}, {Readable("c.ended_at")}, c.result, c.result_number, c.dm_state";

        // Every word on the request list, so a read of every kind's claims uses the index on kind and claimed_at
        static readonly string[] AllKinds = [.. RequestKinds.All.Select(kind => kind.Word)];

        private readonly DatabaseNames _names;

        public BotRequestQueue(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // Adds a signed request in the transaction given, and returns its new ID. Refused before any SQL, and so with the audit
        // row that travels with it, unless the request is one the bot would take as it stands: in the dashboard's form, a
        // kind on the list that its role may ask for, lasting no longer than its kind allows, with the details its kind
        // takes, and a signature of the right length.
        public async Task<ulong> AddRequest(RequestFields fields, byte[] signature, DataTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(fields);
            ArgumentNullException.ThrowIfNull(transaction);

            if (signature == null || signature.Length != RequestSigning.SignatureLength)
                throw new ArgumentException($"A request's signature is {RequestSigning.SignatureLength} bytes", nameof(signature));

            if (RequestRules.Problem(fields) is { } problem)
                throw new ArgumentException($"The bot would refuse this request: {problem}", nameof(fields));

            string insert = $@"INSERT INTO {_names.Bot}.bot_commands (request_key, kind, actor_id, actor_name, actor_role, guild_id, channel_id, argument,
                               created_at, expires_at, signature)
                               VALUES (@RequestKey, @Kind, @ActorId, @ActorName, @ActorRole, @GuildId, @ChannelId, @Argument, @CreatedAt, @ExpiresAt, @signature)";

            // The connection's own last insert, which the transaction's other statements leave alone
            const string NewId = "SELECT LAST_INSERT_ID()";

            await SaveDataCount(insert, new
            {
                fields.RequestKey,
                fields.Kind,
                fields.ActorId,
                fields.ActorName,
                fields.ActorRole,
                fields.GuildId,
                fields.ChannelId,
                fields.Argument,
                fields.CreatedAt,
                fields.ExpiresAt,
                signature
            }, transaction).ConfigureAwait(false);

            return await LoadFirstData<ulong, object?>(NewId, null, transaction).ConfigureAwait(false);
        }

        public Task<BotRequestRow?> GetRequest(ulong id)
        {
            string sql = $@"SELECT {BotRequestData.RequestColumns}
                            FROM {_names.Bot}.bot_commands
                            WHERE id = @id";

            return LoadFirstData<BotRequestRow?, dynamic>(sql, new { id });
        }

        // By the key the dashboard made, which finds a request whose insert may or may not have committed. A key in another
        // form is refused before any SQL, because the table's collation ignores case and would find one in capitals.
        public Task<BotRequestRow?> GetRequestByKey(string requestKey)
        {
            if (requestKey is not { Length: 32 } || !requestKey.All(char.IsAsciiHexDigitLower))
                throw new ArgumentException("A request's key is 32 hexadecimal digits in small letters", nameof(requestKey));

            string sql = $@"SELECT {BotRequestData.RequestColumns}
                            FROM {_names.Bot}.bot_commands
                            WHERE request_key = @requestKey";

            return LoadFirstData<BotRequestRow?, dynamic>(sql, new { requestKey });
        }

        // Null when no request has changed them yet, which means posts aren't paused and the status is the default
        public Task<BotSettingsRow?> GetSettings()
        {
            string sql = $@"SELECT {BotRequestData.SettingsColumns}
                            FROM {_names.Bot}.bot_settings
                            WHERE id = 1";

            return LoadFirstData<BotSettingsRow?, object?>(sql, null);
        }

        // On MinerBot's own connection, so a request and its audit row commit together. The database is named first, so a
        // missing BotConnection fails with that reason before any connection is tried.
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            _ = _names.Bot;
            return InTransaction(work);
        }

        // The request with this ID as its page shows it, null when there's none
        public Task<RequestPageRow?> GetRequestView(ulong id)
        {
            string sql = $@"SELECT {PageColumns}
                            FROM {_names.Bot}.bot_commands c
                            WHERE c.id = @id";

            return LoadFirstData<RequestPageRow?, dynamic>(sql, new { id });
        }

        // The newest request of each kind given that has one, oldest first, and only that server's when one is given: who
        // asked for each control last, when, and how the bot answered. The kinds are checked against the request list first,
        // as CountClaims checks them.
        public async Task<IReadOnlyList<RequestPageRow>> ListLatest(IReadOnlyCollection<string> kinds, ulong? guildId = null)
        {
            if (kinds == null || kinds.Count == 0)
                throw new ArgumentException("A read of the latest requests names at least one kind", nameof(kinds));

            if (kinds.Any(kind => RequestKinds.Find(kind) == null))
                throw new ArgumentException("A read of the latest requests names only kinds on the request list", nameof(kinds));

            string sql = $@"SELECT {PageColumns}
                            FROM {_names.Bot}.bot_commands c
                            JOIN (SELECT MAX(id) AS id
                                  FROM {_names.Bot}.bot_commands
                                  WHERE kind IN @kinds{(guildId != null ? " AND guild_id = @guildId" : "")}
                                  GROUP BY kind) latest ON c.id = latest.id
                            ORDER BY c.id";

            var rows = await LoadDataEnum<RequestPageRow, dynamic>(sql, new { kinds, guildId }).ConfigureAwait(false);
            return rows.ToList();
        }

        // Every request any run took since the time given, of every kind and however it ended: the rows the bot counts for
        // its limits. A decision never sets claimed_at, so refused and expired requests aren't here. The dashboard narrows
        // them to each kind's window, the kinds counted with it and its server, as BotRequestData.CountClaims counts, to
        // check the bot's limit before it writes a request the bot would refuse. Its own statement, so the bot's file stays
        // as it is.
        public async Task<IReadOnlyList<ClaimRow>> ListClaimsSince(DateTime since)
        {
            RequireUtc(since, nameof(since));

            string sql = $@"SELECT id, kind, guild_id, {Readable("claimed_at")}
                            FROM {_names.Bot}.bot_commands
                            WHERE kind IN @kinds AND claimed_at >= @since";

            var rows = await LoadDataEnum<ClaimRow, dynamic>(sql, new { kinds = AllKinds, since }).ConfigureAwait(false);
            return rows.ToList();
        }

        // The requests that wait for the bot and haven't expired by the time given, oldest first. The bot counts each one
        // toward its limit once it takes it.
        public async Task<IReadOnlyList<WaitingRow>> ListWaiting(DateTime now)
        {
            RequireUtc(now, nameof(now));

            string sql = $@"SELECT id, kind, guild_id, {Readable("created_at")}, {Readable("expires_at")}
                            FROM {_names.Bot}.bot_commands
                            WHERE state IS NULL AND expires_at > @now
                            ORDER BY id";

            var rows = await LoadDataEnum<WaitingRow, dynamic>(sql, new { now }).ConfigureAwait(false);
            return rows.ToList();
        }

        // What one person asked for since the time given, oldest first, in any state, refused and expired included, since
        // each one was sent: what the dashboard's limit on each person counts
        public async Task<IReadOnlyList<AskedRow>> ListAskedBy(ulong actorId, DateTime since)
        {
            RequireUtc(since, nameof(since));

            string sql = $@"SELECT kind, {Readable("created_at")}
                            FROM {_names.Bot}.bot_commands
                            WHERE created_at >= @since AND actor_id = @actorId
                            ORDER BY id";

            var rows = await LoadDataEnum<AskedRow, dynamic>(sql, new { actorId, since }).ConfigureAwait(false);
            return rows.ToList();
        }

        // The newest request whose direct message to the owner went or failed, null while none has one
        public Task<RequestPageRow?> LatestDm()
        {
            string sql = $@"SELECT {PageColumns}
                            FROM {_names.Bot}.bot_commands c
                            WHERE c.dm_state IS NOT NULL
                            ORDER BY c.id DESC
                            LIMIT 1";

            return LoadFirstData<RequestPageRow?, object?>(sql, null);
        }

        // The newest sign-in alert the bot refused, null while it has refused none. Every sign-in writes an alert, the owner's
        // own too, so the latest alert soon isn't the refused one; this one stays until the bot refuses another. The index on
        // state and id finds it.
        public Task<RequestPageRow?> LatestRefusedAlert()
        {
            string sql = $@"SELECT {PageColumns}
                            FROM {_names.Bot}.bot_commands c
                            WHERE c.state = @refused AND c.kind = @alert
                            ORDER BY c.id DESC
                            LIMIT 1";

            return LoadFirstData<RequestPageRow?, dynamic>(sql, new { refused = RequestStates.Refused, alert = RequestKinds.OwnerSigninAlert });
        }

        // How many sign-in alerts the dashboard wrote since the time given, in any state
        public Task<long> CountAlertsSince(DateTime since)
        {
            RequireUtc(since, nameof(since));

            string sql = $@"SELECT COUNT(*)
                            FROM {_names.Bot}.bot_commands
                            WHERE kind = @alert AND created_at >= @since";

            return LoadFirstData<long, dynamic>(sql, new { alert = RequestKinds.OwnerSigninAlert, since });
        }

        // A time as the pages read it, NULL for a value no DateTime can hold. MariaDB's default mode lets the dashboard's own
        // login write a time of 0000-00-00, or one with a month or day of 0, and a session that allows invalid dates can
        // store a day past its month's end. MySql.Data throws reading any of them, so one such row, written by hand or by
        // mistake, would take a page down. BlacklistData reads the blacklist's end dates the same way. A WHERE that compares
        // the column needs none of this, because it reads nothing back. The value keeps the column's name.
        static string Readable(string column) =>
            $@"CASE WHEN YEAR({column}) = 0 OR MONTH({column}) = 0 OR DAYOFMONTH({column}) = 0 OR DAYOFMONTH({column}) > DAYOFMONTH(LAST_DAY({column}))
               THEN NULL ELSE {column} END AS {column[(column.LastIndexOf('.') + 1)..]}";
    }
}
