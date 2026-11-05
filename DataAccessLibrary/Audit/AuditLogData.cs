using DataAccessLibrary.Audit.Models;
using System.Text.RegularExpressions;

namespace DataAccessLibrary.Audit
{
    // The audit log in MinerBot's own database, which only ever gets rows added. Its SQL names the table there, so a row
    // can also be written inside a transaction on the website's connection, together with the change it records.
    public class AuditLogData : DbBaseConnection, IAuditLogData
    {
        const int ActionLimit = 50;
        const int TargetTypeLimit = 30;
        const int TargetIdLimit = 40;

        // TEXT holds 65,535 bytes, which can be as few as about 16,000 characters
        const int JsonLimit = 16000;

        const int ActorNameLimit = 100;
        const int DetailLimit = 300;
        const int IpLimit = 45;
        const int RequestIdLimit = 64;

        // Words in small letters joined by dots, such as armor.approve
        static readonly Regex Action = new(@"^[a-z_]+(\.[a-z_]+)+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private readonly DatabaseNames _names;

        public AuditLogData(ISQLConnectionFactory connectionFactory, DatabaseNames names) : base(connectionFactory, DBConnectionName.Bot)
        {
            _names = names;
        }

        // Checked before any SQL, so a row that can't be saved stops the change it travels with. What identifies the change
        // is refused when it doesn't fit, and text that's only there to read is cut, because strict mode would refuse it.
        public Task AddAudit(AuditEntry entry, DataTransaction? transaction = null)
        {
            if (!AuditRole.IsKnown(entry.actor_role))
                throw new ArgumentException($"An audit row's role is one of: {string.Join(", ", AuditRole.All)}", nameof(entry));

            if (!AuditOutcome.IsKnown(entry.outcome))
                throw new ArgumentException($"An audit row's outcome is one of: {string.Join(", ", AuditOutcome.All)}", nameof(entry));

            if (entry.action == null || entry.action.Length > ActionLimit || !Action.IsMatch(entry.action))
                throw new ArgumentException($"An audit row's action is words in small letters joined by dots, such as armor.approve, in at most {ActionLimit} characters", nameof(entry));

            RequireUtc(entry.at, nameof(entry));
            RequireFits(entry.target_type, TargetTypeLimit, "target type");
            RequireFits(entry.target_id, TargetIdLimit, "target ID");
            RequireFits(entry.before_json, JsonLimit, "JSON before the change");
            RequireFits(entry.after_json, JsonLimit, "JSON after the change");

            string sql = $@"INSERT INTO {_names.Bot}.audit_log (at, actor_id, actor_name, actor_role, action, outcome, guild_id, target_type, target_id,
                            before_json, after_json, detail, ip, request_id)
                            VALUES (@at, @actor_id, @actor_name, @actor_role, @action, @outcome, @guild_id, @target_type, @target_id,
                            @before_json, @after_json, @detail, @ip, @request_id)";

            return SaveDataCount(sql, new
            {
                entry.at,
                entry.actor_id,
                actor_name = Clip(entry.actor_name, ActorNameLimit),
                entry.actor_role,
                entry.action,
                entry.outcome,
                entry.guild_id,
                entry.target_type,
                entry.target_id,
                entry.before_json,
                entry.after_json,
                detail = Clip(entry.detail, DetailLimit),
                ip = Clip(entry.ip, IpLimit),
                request_id = Clip(entry.request_id, RequestIdLimit)
            }, transaction);
        }

        // Newest first, with the order the rows were written breaking ties
        public Task<Page<AuditEntry>> ListAudit(AuditQuery query)
        {
            var (take, skip) = RequirePage(query.Page, query.PageSize);
            DateTime? from = query.From is { } start ? RequireUtc(start, nameof(query.From)) : null;
            DateTime? to = query.To is { } end ? RequireUtc(end, nameof(query.To)) : null;

            string where = @"WHERE (@actorId IS NULL OR actor_id = @actorId)
                            AND (@action IS NULL OR action = @action)
                            AND (@guildId IS NULL OR guild_id = @guildId)
                            AND (@targetType IS NULL OR target_type = @targetType)
                            AND (@targetId IS NULL OR target_id = @targetId)
                            AND (@from IS NULL OR at >= @from)
                            AND (@to IS NULL OR at < @to)";

            string sql = $@"SELECT id, at, actor_id, actor_name, actor_role, action, outcome, guild_id, target_type, target_id,
                            before_json, after_json, detail, ip, request_id
                            FROM {_names.Bot}.audit_log
                            {where}
                            ORDER BY at DESC, id DESC
                            LIMIT @take OFFSET @skip;
                            SELECT COUNT(*)
                            FROM {_names.Bot}.audit_log
                            {where};";

            return LoadPage<AuditEntry, dynamic>(sql, new
            {
                actorId = query.ActorId,
                action = query.Action,
                guildId = query.GuildId,
                targetType = query.TargetType,
                targetId = query.TargetId,
                from,
                to,
                take,
                skip
            });
        }

        static void RequireFits(string? text, int limit, string what)
        {
            if (text != null && CodePoints(text) > limit)
                throw new ArgumentException($"An audit row's {what} holds at most {limit} characters", nameof(text));
        }
    }
}
