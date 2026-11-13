using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit.Models;
using DataAccessLibrary.Blacklist.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Blacklist;

namespace MinerBot.Dashboard.Tests.Support
{
    // An entry's page as a browser got it: the form's token, the version each of its forms sends back, and what its edit
    // form's boxes hold
    internal sealed record EntryPage(string Html, string Token, string Version, string RemoveVersion, IReadOnlyDictionary<string, string> Boxes)
    {
        public string Text => WebUtility.HtmlDecode(Html);
    }

    // Blacklist entries as the website's table holds them, and an editor's steps through the blacklist's pages
    internal static partial class BlacklistSteps
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // 12345 written as a SteamID64, which is what the list shows
        public const string SteamId64 = "76561197960278073";

        public static BlacklistModel Entry(uint account, string? name = "Test Player", string? reason = "Testing", DateTime? expires = null, string? notes = "Made up") => new()
        {
            steam_id = account,
            username = name,
            reason = reason,
            expire_date = expires,
            notes = notes
        };

        public static async Task AddEntriesAsync(this MariaDbFixture db, params BlacklistModel[] entries)
        {
            foreach (var entry in entries)
                await db.Blacklist().InsertPlayerBan(entry);
        }

        // As the entry is stored, read the way the pages read it
        public static Task<BlacklistModel?> EntryAsync(this MariaDbFixture db, uint account) => db.Blacklist().GetBannedPlayer(account);

        public static async Task<long> EntryCountAsync(this MariaDbFixture db) =>
            (await db.RootQueryAsync<long>($"SELECT COUNT(*) FROM {MariaDbFixture.BlacklistTable}")).Single();

        public static async Task<List<AuditEntry>> BlacklistAuditAsync(this MariaDbFixture db) =>
            (await db.AuditAsync()).Where(row => row.action.StartsWith("blacklist.", StringComparison.Ordinal)).ToList();

        public static async Task<EntryPage> OpenAsync(TestBrowser browser, uint account)
        {
            var response = await browser.Client.GetAsync($"/admin/blacklist/{account}", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"The entry's page answered {response.StatusCode}");
            return Read(html);
        }

        // Both forms carry the same version, but on the page answering a refused field, whose edit form keeps the version
        // its boxes came with. The boxes hold what a browser's would: the HTML parser reads a reference to NUL or a C1
        // control as another character, and a text box drops line breaks from its value. BlacklistFormTests pins the
        // parser's table, which this shares with the code under test.
        public static EntryPage Read(string html) => new(
            html,
            SignInSteps.FormToken(html),
            FormVersion(html, "Edit"),
            FormVersion(html, "Remove"),
            new[] { "name", "reason", "expires", "notes" }.ToDictionary(box => box, box => AsBrowserSends(BoxValue(html, box) ?? "")));

        static string AsBrowserSends(string value) => string.Concat(value.Where(c => c is not ('\r' or '\n')).Select(BlacklistForm.AsParsed));

        // The version a form sends back, or empty when the page has no such form
        static string FormVersion(string html, string handler) =>
            Regex.Match(html, $"handler={handler}\"[^>]*>\\s*<input type=\"hidden\" name=\"version\" value=\"([0-9a-f]*)\"").Groups[1].Value;

        // The edit form as a browser sends it: the version and every box as the page filled it, but for the fields given
        public static Task<HttpResponseMessage> EditAsync(TestBrowser browser, uint account, EntryPage page, params (string Name, string Value)[] fields)
        {
            var boxes = new Dictionary<string, string>(page.Boxes);

            foreach (var (name, value) in fields)
                boxes[name] = value;

            var sent = new List<KeyValuePair<string, string>> { new("version", page.Version) };
            sent.AddRange(boxes);

            return SignInSteps.PostAsync(browser, $"/admin/blacklist/{account}?handler=Edit", page.Token, origin: DashboardFactory.PublicOrigin, fields: sent);
        }

        public static Task<HttpResponseMessage> RemoveAsync(TestBrowser browser, uint account, EntryPage page) =>
            SignInSteps.PostAsync(browser, $"/admin/blacklist/{account}?handler=Remove", page.Token, origin: DashboardFactory.PublicOrigin,
                fields: [new("version", page.RemoveVersion)]);

        // The add form, with its token from the page, and exactly these fields
        public static async Task<HttpResponseMessage> AddAsync(TestBrowser browser, params (string Name, string Value)[] fields)
        {
            var token = SignInSteps.FormToken(await browser.Client.GetStringAsync("/admin/blacklist/add", Token));

            return await SignInSteps.PostAsync(browser, "/admin/blacklist/add", token, origin: DashboardFactory.PublicOrigin,
                fields: fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));
        }

        // A form someone made up, with exactly these fields
        public static Task<HttpResponseMessage> CraftedAsync(TestBrowser browser, string path, string token, params (string Name, string Value)[] fields) =>
            SignInSteps.PostAsync(browser, path, token, origin: DashboardFactory.PublicOrigin,
                fields: fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

        // A box's value as the page wrote it, decoded, or null when the page has no such box
        public static string? BoxValue(string html, string name)
        {
            var match = Regex.Match(html, $"<input[^>]*\\bname=\"{name}\"[^>]*\\bvalue=\"([^\"]*)\"");
            return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        // The rows of the first audit table on a page
        public static int AuditRowCount(string html)
        {
            var table = Regex.Match(html, "<table class=\"audit-table\">.*?</table>", RegexOptions.Singleline);
            return table.Success ? Regex.Count(Regex.Match(table.Value, "<tbody>.*</tbody>", RegexOptions.Singleline).Value, "<tr>") : 0;
        }
    }
}
