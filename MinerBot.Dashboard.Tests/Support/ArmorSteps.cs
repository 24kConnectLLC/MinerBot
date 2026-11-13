using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Armor.Models;
using DataAccessLibrary.Tests.Database;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Tests.Support
{
    // A review page as a browser got it: the form's token, the status and version its forms send back, and the warnings
    // it offers to tick
    internal sealed record ReviewForm(string Html, string Token, string Expected, string Version, IReadOnlyList<string> Ticks)
    {
        public string Text => WebUtility.HtmlDecode(Html);
    }

    // Armor as the website saves it, and a reviewer's steps through the review page
    internal static partial class ArmorSteps
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        public const string Screenshot = "https://murderminershub.com/wp-content/uploads/ArmorPreviews/Test-Armor.png";

        // A good submission, waiting for a review, that the rules find nothing in. Each test changes what it needs.
        public static ArmorSubmission Submission(long id, string? status = ArmorStatus.Pending, string name = "Test Armor") => new()
        {
            ID = id,
            CreatedAt = new DateTime(2026, 11, 1, 12, 0, 0).AddMinutes(id),
            Status = status,
            Author = "Test Author",
            ArmorName = name,
            ArmorDescription = "A dark set for night raids.",
            ArmorScreenshot = Screenshot,
            ArmorChoice = "Bug",
            ArmorSkin = "Default",
            Helmet = 1,
            Visor = 2,
            Torso = 3,
            Shoulders = 4,
            Biceps = 5,
            Forearms = 6,
            Hands = 7,
            Pelvis = 8,
            Thighs = 9,
            Legs = 10,
            Boots = 11,
            Shields = 12,
            Other = 13
        };

        public static async Task AddAsync(this MariaDbFixture db, params ArmorSubmission[] armors)
        {
            foreach (var armor in armors)
                await db.AddArmorAsync(armor);
        }

        // A signed-in Owner or Admin, at an address of its own
        public static async Task<TestBrowser> ReviewerAsync(DashboardFactory factory, string address, TestUser? user = null)
        {
            var browser = factory.Visitor(address);
            await SignInSteps.SignedInAsync(factory, browser, user ?? TestUsers.Admin);
            return browser;
        }

        public static async Task<ReviewForm> OpenAsync(TestBrowser browser, long id)
        {
            var response = await browser.Client.GetAsync($"/admin/armor/{id}", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"The review page answered {response.StatusCode}");
            return Read(html);
        }

        // Each form on the page carries the same status and version
        public static ReviewForm Read(string html) => new(
            html,
            SignInSteps.FormToken(html),
            Hidden(html, "expected"),
            Hidden(html, "version"),
            TickBoxes().Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToList());

        // One of the page's forms, as a browser sends it from the dashboard: its status and version, every warning ticked
        // unless the test says otherwise, and any other fields
        public static Task<HttpResponseMessage> PostAsync(TestBrowser browser, long id, string handler, ReviewForm form, bool tickAll = true,
            params (string Name, string Value)[] fields)
        {
            var sent = new List<KeyValuePair<string, string>> { new("expected", form.Expected), new("version", form.Version) };

            if (tickAll && handler == "Approve")
                sent.AddRange(form.Ticks.Select(tick => new KeyValuePair<string, string>("ticks", tick)));

            sent.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

            return SignInSteps.PostAsync(browser, $"/admin/armor/{id}?handler={handler}", form.Token, origin: DashboardFactory.PublicOrigin, fields: sent);
        }

        // A form someone made up, with exactly these fields
        public static Task<HttpResponseMessage> CraftedAsync(TestBrowser browser, string path, string token, params (string Name, string Value)[] fields) =>
            SignInSteps.PostAsync(browser, path, token, origin: DashboardFactory.PublicOrigin,
                fields: fields.Select(field => new KeyValuePair<string, string>(field.Name, field.Value)));

        public static async Task<string> TextAsync(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        static string Hidden(string html, string name)
        {
            var match = Regex.Match(html, $"<input type=\"hidden\" name=\"{name}\" value=\"([^\"]*)\"");
            Assert.True(match.Success, $"The page has no {name} field");
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }

        [GeneratedRegex("""<input type="checkbox"[^>]*name="ticks" value="([^"]+)"[^>]*>""")]
        private static partial Regex TickBoxes();
    }
}
