using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The pages work without scripts, pass the Content-Security-Policy as they are, and speak plain English
    [Collection("Database")]
    public partial class PageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static readonly string[] Reasons = ["unlisted", "mfa", "scope", "cancelled", "failed"];

        // Fails on markup the policy would block, such as an inline script or style, or on a page that needs a script
        static void AssertAccessible(string what, string html)
        {
            Assert.True(html.Contains("<html lang=\"en\">", StringComparison.Ordinal), $"{what} has no lang");
            Assert.True(Regex.Count(html, "<h1[ >]") == 1, $"{what} hasn't exactly one h1");
            Assert.True(html.Contains("<main id=\"main\"", StringComparison.Ordinal), $"{what} has no main");
            Assert.True(html.Contains("href=\"#main\"", StringComparison.Ordinal), $"{what} has no skip link");
            Assert.True(NonEmptyTitle().IsMatch(html), $"{what} has no title");
            Assert.True(html.Contains("<meta name=\"color-scheme\" content=\"light dark\"", StringComparison.Ordinal), $"{what} has no color-scheme");
            Assert.True(html.Contains("/css/site.css?v=", StringComparison.Ordinal), $"{what} has no versioned stylesheet");

            foreach (var blocked in new[] { "<script", " style=", "<style" })
                Assert.False(html.Contains(blocked, StringComparison.OrdinalIgnoreCase), $"{what} has {blocked}");
        }

        [Fact]
        public async Task EveryPageIsAccessibleAndScriptFree()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var anonymous = factory.Visitor();

            foreach (var page in new[] { "/", "/signout", "/denied", "/error", "/nothing-here" }.Concat(Reasons.Select(reason => "/denied?reason=" + reason)))
                AssertAccessible(page, await (await anonymous.Client.GetAsync(page, Token)).Content.ReadAsStringAsync(Token));

            var owner = factory.Visitor("203.0.113.70");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);

            foreach (var page in new[] { "/", "/admin", "/signout" })
                AssertAccessible("owner " + page, await owner.Client.GetStringAsync(page, Token));
        }

        // A link, not a form: browsers apply the form-action policy to the redirect after a form, and discord.com isn't 'self'
        [Fact]
        public async Task HomeSignInIsALink()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var html = await factory.Visitor().Client.GetStringAsync("/", Token);

            Assert.Contains("href=\"/signin?returnUrl=%2Fadmin\"", html);
            Assert.Contains("Sign in with Discord", html);
            Assert.DoesNotContain("<form", html);
            Assert.Contains("MinerBot is a Discord bot for Murder Miners", html);
            Assert.Contains("two-factor", html);
        }

        [Fact]
        public async Task DeniedExplainsEachReason()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var visitor = factory.Visitor();
            var sentences = new Dictionary<string, string>
            {
                ["unlisted"] = "Your Discord account isn't on the dashboard's list",
                ["mfa"] = "Turn it on in Discord under User Settings, My Account",
                ["scope"] = "Discord gave the dashboard different permissions",
                ["cancelled"] = "You cancelled the sign-in at Discord",
                ["failed"] = "The sign-in with Discord didn't finish"
            };

            foreach (var (reason, sentence) in sentences)
            {
                var html = WebUtility.HtmlDecode(await visitor.Client.GetStringAsync("/denied?reason=" + reason, Token));

                Assert.Contains(sentence, html);
                Assert.All(sentences.Where(other => other.Key != reason), other => Assert.DoesNotContain(other.Value, html));
            }

            // Only the people on the list can use it, so signing in again is offered to everyone else
            Assert.DoesNotContain("Sign in again", await visitor.Client.GetStringAsync("/denied?reason=unlisted", Token));
            Assert.Contains("Sign in again", await visitor.Client.GetStringAsync("/denied?reason=mfa", Token));

            // A reason the page doesn't know is never shown as it came
            var crafted = await visitor.Client.GetStringAsync("/denied?reason=" + Uri.EscapeDataString("<b>x</b>"), Token);
            Assert.Contains("You can't open that page.", WebUtility.HtmlDecode(crafted));
            Assert.DoesNotContain("<b>x</b>", WebUtility.HtmlDecode(crafted));
            Assert.DoesNotContain("<b>x</b>", crafted);
        }

        [Fact]
        public async Task OverviewShowsWhoIsSignedIn()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var owner = factory.Visitor("203.0.113.71");
            await SignInSteps.SignedInAsync(factory, owner, TestUsers.Owner);
            var ownerPage = WebUtility.HtmlDecode(await owner.Client.GetStringAsync("/admin", Token));
            Assert.Contains($"You're signed in as Test Owner, Discord ID {TestUsers.Owner.IdText}, with the Owner role.", ownerPage);

            var admin = factory.Visitor("203.0.113.72");
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);
            var adminPage = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin", Token));
            Assert.Contains($"You're signed in as Test Admin, Discord ID {TestUsers.Admin.IdText}, with the Admin role.", adminPage);
            Assert.Contains("Signed in as Test Admin (Admin)", adminPage);
        }

        // Discord names are anyone's to choose, so they're only ever shown encoded
        [Fact]
        public async Task DisplayNameIsEncoded()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var admin = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin with { GlobalName = "<script>alert(1)</script>" });

            var html = await admin.Client.GetStringAsync("/admin", Token);

            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        }

        [Fact]
        public async Task StylesheetFollowsTheDevice()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await factory.Visitor().Client.GetAsync("/css/site.css", Token);
            var css = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("prefers-color-scheme: dark", css);
            Assert.Contains("color-scheme: light dark", css);
            Assert.Contains(":focus-visible", css);
        }

        [Fact]
        public async Task SignOutPageWhenSignedOut()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var html = WebUtility.HtmlDecode(await factory.Visitor().Client.GetStringAsync("/signout", Token));

            Assert.Contains("You're not signed in.", html);
            Assert.DoesNotContain("<form", html);
        }

        [GeneratedRegex("<title>[^<]*\\S[^<]*</title>")]
        private static partial Regex NonEmptyTitle();
    }
}
