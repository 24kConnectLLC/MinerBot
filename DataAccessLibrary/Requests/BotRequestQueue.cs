using DataAccessLibrary.Requests.Models;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Requests
{
    // The dashboard's requests to the bot, in MinerBot's own database, as the dashboard writes and reads them. Its login may
    // add a request only by naming the columns the dashboard fills in, so the bot's columns start NULL, and it can never
    // change or delete one. A request goes in with its audit row, in one transaction on MinerBot's own connection.
    public class BotRequestQueue : DbBaseConnection, IBotRequestQueue
    {
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
    }
}
