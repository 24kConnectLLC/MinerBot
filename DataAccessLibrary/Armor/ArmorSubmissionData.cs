using DataAccessLibrary.Armor.Models;
using MinerBot.Shared.Armor;

namespace DataAccessLibrary.Armor
{
    // Armor submissions whatever their status, for reviewing them. The bot's own queries stay in ArmorData.
    public class ArmorSubmissionData : DbBaseConnection, IArmorSubmissionData
    {
        // The status as the bot's queries and the list's tabs read it. Their Status = 'Approved' goes through the column's
        // collation, which also matches 'approved ' or 'APPROVED', so the same comparison here decides which word a row has,
        // rather than the dashboard guessing at the collation's rules.
        const string KnownStatus = $@"CASE WHEN Status = '{ArmorStatus.Pending}' THEN '{ArmorStatus.Pending}'
                            WHEN Status = '{ArmorStatus.Approved}' THEN '{ArmorStatus.Approved}'
                            WHEN Status = '{ArmorStatus.NotApproved}' THEN '{ArmorStatus.NotApproved}' END AS KnownStatus";

        // Every column, in two parts around the screenshot, which lists read differently
        const string ColumnsBefore = $"ID, CreatedAt, UserID, Status, {KnownStatus}, Author, ArmorName, ArmorDescription";
        const string ColumnsAfter = @"ArmorChoice, ArmorSkin,
                            Helmet, Visor, Torso, Shoulders, Biceps, Forearms, Hands, Pelvis, Thighs, Legs, Boots, Shields, Other, ColorArray";

        const string Columns = $"{ColumnsBefore}, ArmorScreenshot, {ColumnsAfter}";

        // The website's column has no limit, so a list cuts the address one character past what the rules look at: a page
        // can't load megabytes from a row the form let through, and the rules still see it's too long. GetSubmission reads
        // the whole value.
        static readonly string ListColumns = $"{ColumnsBefore}, LEFT(ArmorScreenshot, {ArmorRules.ScreenshotLimit + 1}) AS ArmorScreenshot, {ColumnsAfter}";

        public ArmorSubmissionData(ISQLConnectionFactory connectionFactory) : base(connectionFactory, DBConnectionName.Armor)
        {
        }

        // A null status lists every row, whatever its status
        public Task<Page<ArmorSubmission>> ListByStatus(string? status, int page, int pageSize)
        {
            return Search("", status, page, pageSize);
        }

        // By name or author, ignoring capitals through the table's collation. Pending armor comes oldest first, because it's
        // waiting, and the rest newest first. ID breaks ties, because CreatedAt only has whole seconds.
        public Task<Page<ArmorSubmission>> Search(string text, string? status, int page, int pageSize)
        {
            RequireStatus(status, nameof(status), allowNone: true);
            var (take, skip) = RequirePage(page, pageSize);

            var search = (text ?? "").Trim();
            var pattern = search.Length == 0 ? null : $"%{EscapeLike(search)}%";
            var order = status == ArmorStatus.Pending ? "ORDER BY CreatedAt, ID" : "ORDER BY CreatedAt DESC, ID DESC";

            string where = @"WHERE (@status IS NULL OR Status = @status)
                            AND (@pattern IS NULL OR ArmorName LIKE @pattern ESCAPE '!' OR Author LIKE @pattern ESCAPE '!')";

            string sql = $@"SELECT {ListColumns}
                            FROM ihvph_armor_styles
                            {where}
                            {order}
                            LIMIT @take OFFSET @skip;
                            SELECT COUNT(*)
                            FROM ihvph_armor_styles
                            {where};";

            return LoadPage<ArmorSubmission, dynamic>(sql, new { status, pattern, take, skip });
        }

        // In a transaction it's a locking read of the one row, so the website can't change the armor between a review's
        // checks of it and the change of its status. An approval runs IsApprovedNameTaken first, which already holds it.
        public Task<ArmorSubmission?> GetSubmission(long id, DataTransaction? transaction = null)
        {
            string sql = $@"SELECT {Columns}
                            FROM ihvph_armor_styles
                            WHERE ID = @id";

            if (transaction != null)
                sql += " FOR UPDATE";

            return LoadFirstData<ArmorSubmission?, dynamic>(sql, new { id }, transaction);
        }

        // Only while the armor still has the status the caller saw, so when two reviewers act at once one of them wins and
        // the other is told. A null expected status is an armor that has none. The comparison goes through the column's
        // collation, as KnownStatus does, so an expected Approved also matches a status stored as 'approved ', and the
        // change saves the website's own word.
        public async Task<bool> ChangeStatus(long id, string? expectedStatus, string newStatus, DataTransaction? transaction = null)
        {
            RequireStatus(newStatus, nameof(newStatus), allowNone: false);
            RequireStatus(expectedStatus, nameof(expectedStatus), allowNone: true);

            // The count would say it applied, and nothing would have changed
            if (expectedStatus == newStatus)
                throw new ArgumentException("The new status is the one the armor already has", nameof(newStatus));

            // The bot reads IDs as an int, so one approved armor past that would stop its armor posts in every server.
            // ArmorRules tells the reviewer why; this stops a caller that skipped the rules.
            if (newStatus == ArmorStatus.Approved && id is < int.MinValue or > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(id), id, "The bot can't read an armor with this ID, so it can't be approved");

            string sql = @"UPDATE ihvph_armor_styles
                            SET Status = @newStatus
                            WHERE ID = @id AND Status <=> @expectedStatus";

            return await SaveDataCount(sql, new { id, expectedStatus, newStatus }, transaction).ConfigureAwait(false) == 1;
        }

        // The same way /armor lookup finds a name: through the column's collation, which ignores capitals, accents and
        // trailing spaces, and characters it gives no weight, such as zero-width spaces. TRIM takes off spaces in front too.
        // In a transaction it's a locking read. Status has no index, so it locks every row, and the space for new ones,
        // until the transaction ends: a second approval's check waits for the first to commit, then sees its name. Run it
        // before anything else touches the armor table in that transaction, or two approvals that each changed their own
        // row first would deadlock.
        public async Task<bool> IsApprovedNameTaken(string name, long exceptId, DataTransaction? transaction = null)
        {
            string sql = @"SELECT COUNT(*)
                            FROM ihvph_armor_styles
                            WHERE Status = @approved AND ID <> @exceptId AND TRIM(ArmorName) = TRIM(@name)";

            if (transaction != null)
                sql += " FOR UPDATE";

            return await LoadFirstData<long, dynamic>(sql, new { approved = ArmorStatus.Approved, name, exceptId }, transaction).ConfigureAwait(false) != 0;
        }

        // On the website's connection. The bot's tables are named with their database, so a review and an audit row can
        // commit together with the change.
        public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work)
        {
            return InTransaction(work);
        }

        // Only the website's own words, so a typo can't save a fourth one
        internal static void RequireStatus(string? status, string name, bool allowNone)
        {
            if (status == null ? !allowNone : !ArmorStatus.IsKnown(status))
                throw new ArgumentException($"An armor's status is one of: {string.Join(", ", ArmorStatus.All)}", name);
        }
    }
}
