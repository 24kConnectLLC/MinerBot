using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Tests.Support;
using MinerBot.Shared.Armor;
using static MinerBot.Dashboard.Tests.Support.ArmorSteps;

namespace MinerBot.Dashboard.Tests
{
    // The review page at /admin/armor/{id}: the submission with its hidden tricks shown, the screenshot only from the
    // website's uploads, the colours and save string, the rules, the bot's two messages, and the history
    [Collection("Database")]
    public class ArmorReviewPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Markup with each run of spaces and line ends made one space, for what spans the page's own indented lines
        static string Squeezed(string html) => Regex.Replace(html, @"\s+", " ");

        [Fact]
        public async Task PageShowsTheSubmissionItsArmorAndItsHistory()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(11, ArmorStatus.Pending, "Ninja"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.120");

            var page = await OpenAsync(admin, 11);
            var text = page.Text;

            Assert.Contains("<title>Armor 11 - MinerBot dashboard</title>", page.Html);
            Assert.Contains("Ninja", text);
            Assert.Contains("A dark set for night raids.", text);
            Assert.Contains("Test Author", text);
            Assert.Contains("Pending Approval", text);
            Assert.Contains("2026-11-01 12:11, by the website's clock", text);
            Assert.Contains("<dt>Helmet</dt>", page.Html);
            Assert.Contains("<th scope=\"row\">Shoulders</th>", page.Html);
            Assert.Contains("<td>4</td>", page.Html);
            Assert.Contains("<code>AQIDBAUGBwgJCgsMDQ==</code>, from the colours, as the bot works it out.", text);
            Assert.Contains("The rules found nothing to stop or question an approval, the name check included.", text);
            Assert.Contains("No reviews on the dashboard yet.", text);

            // A clean pending armor offers Approve and Reject, each carrying the status and version the page shows
            Assert.Equal(ArmorStatus.Pending, page.Expected);
            Assert.Matches("^[0-9a-f]{64}$", page.Version);
            Assert.Contains("action=\"/admin/armor/11?handler=Approve\"", page.Html);
            Assert.Contains("action=\"/admin/armor/11?handler=Reject\"", page.Html);
            Assert.DoesNotContain("handler=TakeDown", page.Html);
            Assert.DoesNotContain("handler=BackToPending", page.Html);
            Assert.Empty(page.Ticks);
        }

        // Fails if submitter text reaches the page as markup, turns the page's words around, hides a character, or gets
        // into the title
        [Fact]
        public async Task HiddenTricksAreShownAndNothingIsRawMarkup()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(12, ArmorStatus.Pending, "<b>Bold</b>‮txt.exe");
            armor.ArmorDescription = "Ping @everyone at https://example.com​";
            armor.Author = "<i>Test</i> <@1000000000000000001>";
            armor.ArmorChoice = "<u>Bug</u>⁦";
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.121");

            var html = (await OpenAsync(admin, 12)).Html;

            foreach (var markup in new[] { "<b>Bold", "<i>Test", "<u>Bug" })
                Assert.DoesNotContain(markup, html);

            Assert.Contains("<title>Armor 12 - MinerBot dashboard</title>", html);
            Assert.Contains("<bdi class=\"revealed\">&lt;b&gt;Bold&lt;/b&gt;<mark class=\"hidden-char\" title=\"right-to-left override\">[U&#x2B;202E]</mark>txt.exe</bdi>", html);
            Assert.Contains("<mark class=\"mention\" title=\"Discord mention\">@everyone</mark>", html);
            Assert.Contains("<mark class=\"link\" title=\"Link\">https://example.com</mark><mark class=\"hidden-char\" title=\"zero-width space\">[U&#x2B;200B]</mark>", html);
            Assert.Contains("<mark class=\"mention\" title=\"Discord mention\">&lt;@1000000000000000001&gt;</mark>", html);
            Assert.Contains("&lt;u&gt;Bug&lt;/u&gt;<mark class=\"hidden-char\" title=\"left-to-right isolate\">[U&#x2B;2066]</mark>", html);

            // The previews show it raw, as Discord would, but encoded and isolated
            Assert.Contains("<p class=\"embed-title\">", html);
            Assert.Contains("<bdi>&lt;b&gt;Bold&lt;/b&gt;&#x202E;txt.exe</bdi>", html);
        }

        // The owner's decision: only an https upload on the website, which the Content-Security-Policy allows too, and
        // nothing else is ever loaded. Anything else is shown as text, with why.
        [Theory]
        [InlineData("http://127.0.0.1:8080/admin")]
        [InlineData("https://example.com/wp-content/uploads/x.png")]
        [InlineData("https://murderminershub.com/wp-login.php?action=logout")]
        [InlineData("https://murderminershub.com/wp-content/uploads/%2e%2e/%2e%2e/wp-admin/")]
        public async Task ScreenshotThatIsntAnUploadIsShownAsText(string address)
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(13);
            armor.ArmorScreenshot = address;
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.122");

            var page = await OpenAsync(admin, 13);

            Assert.DoesNotContain("<img", page.Html);
            Assert.Contains("Not shown. It isn't an upload on the website, an https address under /wp-content/uploads/ on murderminershub.com", page.Text);
            Assert.Contains(ArmorWords.ScreenshotBlocksApproval, page.Text);
            Assert.Contains("<p class=\"address\"><bdi class=\"revealed\">", page.Html);
            Assert.Contains(address, page.Text);
            Assert.Contains("the bot fetches an approved armor's screenshot from the server's own network on every /armor lookup", page.Text);
            Assert.DoesNotContain("handler=Approve", page.Html);

            // What to do about it, by the reject form that's offered
            Assert.Contains(ArmorWords.NotOffered, page.Text);
            Assert.Contains(ArmorWords.ScreenshotStep(ArmorStatus.Pending), page.Text);
            Assert.Contains("handler=Reject", page.Html);
        }

        // Armor approved before the rule, whose screenshot the bot already fetches: the page says so, and offers Take down.
        // Rejected armor isn't told to be rejected again.
        [Fact]
        public async Task ApprovedArmorThatTheRulesWouldStopSaysTakeItDown()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(27, ArmorStatus.Approved);
            armor.ArmorScreenshot = "https://murderminershub.com/x.png";
            var rejected = Submission(34, ArmorStatus.NotApproved, "Test Armor 34");
            rejected.ArmorScreenshot = armor.ArmorScreenshot;
            await db.AddAsync(armor, rejected);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.131");

            var page = await OpenAsync(admin, 27);

            Assert.Contains(ArmorWords.ScreenshotFetchedNow, page.Text);
            Assert.DoesNotContain(ArmorWords.ScreenshotBlocksApproval, page.Text);
            Assert.Contains("<h3>What would stop an approval now</h3>", page.Html);
            Assert.Contains(ArmorWords.ApprovedButBlocked, page.Text);
            Assert.Contains(ArmorWords.ScreenshotStep(ArmorStatus.Approved), page.Text);
            Assert.DoesNotContain(ArmorWords.NotOffered, page.Text);
            Assert.Contains("handler=TakeDown", page.Html);

            var notApproved = (await OpenAsync(admin, 34)).Text;
            Assert.Contains(ArmorWords.ScreenshotBlocksApproval, notApproved);
            Assert.Contains(ArmorWords.NotOffered, notApproved);
            Assert.Contains(ArmorWords.ScreenshotStep(ArmorStatus.NotApproved), notApproved);
            Assert.Contains("otherwise leave this armor as it is", notApproved);
        }

        [Fact]
        public async Task UploadIsShownAndAMissingOneSaysSo()
        {
            var db = await fixture.ReadyAsync();
            var none = Submission(15);
            none.ArmorScreenshot = null;
            var huge = Submission(16);
            huge.ArmorScreenshot = Screenshot + new string('a', 100000);
            await db.AddAsync(Submission(14), none, huge);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.123");

            var shown = (await OpenAsync(admin, 14)).Html;
            Assert.Contains($"<img src=\"{Screenshot}\" alt=\"The armor's screenshot, as the website saved it\" />", shown);
            Assert.Contains($"<img class=\"embed-image\" src=\"{Screenshot}\"", shown);

            var missing = await OpenAsync(admin, 15);
            Assert.DoesNotContain("<img", missing.Html);
            Assert.Contains("There's no screenshot.", missing.Text);
            Assert.Equal(["NoScreenshot:Screenshot"], missing.Ticks);

            // An address the website's column let through at any length is cut short on the page
            var cut = await OpenAsync(admin, 16);
            Assert.DoesNotContain("<img", cut.Html);
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"is {huge.ArmorScreenshot.Length:N0} characters long:"), cut.Text);
            Assert.DoesNotContain(new string('a', 400), cut.Text);
            Assert.Contains("…", cut.Text);
        }

        // From the bot's own strings: the post's code block, the card's fields and the footer. ArmorPreviewTests pins the
        // strings themselves.
        [Fact]
        public async Task PreviewsAreTheBotsMessages()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(17, ArmorStatus.Pending, "Ninja"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.124");

            var html = (await OpenAsync(admin, 17)).Html;

            Assert.Contains("<p class=\"embed-title\"> <a href=\"https://murderminershub.com/upload-armor-style/\"><bdi>New Armor Styles:</bdi></a> </p>", Squeezed(html));
            Assert.Contains("<div class=\"embed-description\"><pre class=\"embed-code\"><bdi>Ninja</bdi></pre></div>", html);
            // The encoder writes each line end as &#xA;, which a pre shows as a new line
            Assert.Contains("<pre class=\"embed-code\"><bdi>A dark set for night raids.&#xA;Made by Test Author</bdi></pre>", html);
            Assert.Contains("HELMET       1&#xA;VISOR        2&#xA;", html);
            Assert.Contains("<div class=\"embed-field embed-field-inline\">", html);
            Assert.Contains("<code><bdi>AQIDBAUGBwgJCgsMDQ==</bdi></code>", html);
            Assert.Equal(2, html.Split("<p class=\"embed-footer\"><span class=\"embed-footer-icon\" aria-hidden=\"true\"></span>MinerBot by 24kConnect - Version 2.0</p>").Length - 1);
        }

        // Three backticks the bot didn't put there end its code block in Discord, so the preview says so rather than guess
        [Fact]
        public async Task BacktickInTheDescriptionIsShownAsTheBotSendsIt()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(18);
            armor.ArmorDescription = "Ends here```[click](https://example.com)";
            await db.AddAsync(armor);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.125");

            var page = await OpenAsync(admin, 18);

            Assert.Contains("<pre class=\"embed-raw\">", page.Html);
            Assert.Contains(DiscordText.BrokenNote, page.Text);
            Assert.Contains("Backtick:Description", page.Ticks);
        }

        // One backtick typed for an apostrophe doesn't end Discord's code block, so the card is drawn as servers will see
        // it, with no note that could make a reviewer reject a good armor. It still needs a tick.
        [Fact]
        public async Task SingleBacktickInTheDescriptionIsDrawnInItsBlock()
        {
            var db = await fixture.ReadyAsync();
            var armor = Submission(28);
            armor.ArmorDescription = "Ninja`s night set";
            var author = Submission(29);
            author.Author = "Test Author`";
            await db.AddAsync(armor, author);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.132");

            var page = await OpenAsync(admin, 28);

            Assert.Contains("<pre class=\"embed-code\"><bdi>Ninja`s night set&#xA;Made by Test Author</bdi></pre>", page.Html);
            Assert.DoesNotContain(DiscordText.BrokenNote, page.Text);
            Assert.Contains("Backtick:Description", page.Ticks);

            // An author's last backtick makes three with the block's closing ones, which ends it early
            Assert.Contains(DiscordText.BrokenNote, (await OpenAsync(admin, 29)).Text);
        }

        [Fact]
        public async Task StoredSaveStringIsShownAndFlaggedWhenItDiffers()
        {
            var db = await fixture.ReadyAsync();
            var same = Submission(19, ArmorStatus.Approved);
            same.ColorArray = "AQIDBAUGBwgJCgsMDQ==";
            var other = Submission(20, ArmorStatus.Approved);
            other.ColorArray = "AAAAAAAAAAAAAAAAAA==";
            var broken = Submission(21);
            broken.Torso = 300;
            await db.AddAsync(same, other, broken);
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.126");

            var sameText = (await OpenAsync(admin, 19)).Text;
            Assert.Contains("which the bot shows as it is.", sameText);
            Assert.DoesNotContain("It isn't the one the colours give", sameText);

            // The card would show the stored one, so the rules stop an approval, and for approved armor say what to do
            var otherText = (await OpenAsync(admin, 20)).Text;
            Assert.Contains("It isn't the one the colours give, which is AQIDBAUGBwgJCgsMDQ==.", otherText);
            Assert.Contains("The stored save string isn't the one the colours give", otherText);
            Assert.Contains(ArmorWords.OtherStep(ArmorStatus.Approved), otherText);

            var brokenPage = await OpenAsync(admin, 21);
            Assert.Contains("None: a colour is outside 0 to 255, so the bot couldn't read this armor.", brokenPage.Text);
            Assert.Contains("torso 300", brokenPage.Text);
            Assert.DoesNotContain("handler=Approve", brokenPage.Html);
            Assert.Contains("Approve isn't offered, because of what stops an approval, under Rules.", brokenPage.Text);
            Assert.Contains(ArmorWords.OtherStep(ArmorStatus.Pending), brokenPage.Text);
        }

        // A name another approved armor has: the step says to take that one down first, and links to it on the list
        [Fact]
        public async Task NameTakenSaysWhatToDoAndWhereTheOtherIs()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(30, ArmorStatus.Approved, "Frost Knight"), Submission(31, ArmorStatus.Pending, "frost knight "));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.133");

            var page = await OpenAsync(admin, 31);

            Assert.Contains("An approved armor already has this name", page.Text);
            Assert.Contains(ArmorWords.NameTakenStep(ArmorStatus.Pending), page.Text);
            Assert.Contains("<a href=\"/admin/armor?status=approved&amp;q=frost%20knight\">Look for it among the approved armor</a>", page.Html);
            Assert.DoesNotContain(ArmorWords.ScreenshotStep(ArmorStatus.Pending), page.Text);
            Assert.DoesNotContain(ArmorWords.OtherStep(ArmorStatus.Pending), page.Text);

            // On the list, that search finds the approved one
            var list = await admin.Client.GetStringAsync("/admin/armor?status=approved&q=frost%20knight", Token);
            Assert.Contains("href=\"/admin/armor/30?list=approved&amp;q=frost%20knight\"", list);
        }

        // Approved armor offers Take down and Back to pending, rejected armor Approve and Back to pending, and armor with
        // no status only Back to pending
        [Fact]
        public async Task EachStatusOffersItsOwnActions()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(22, ArmorStatus.Approved, "Test Armor 22"), Submission(23, ArmorStatus.NotApproved, "Test Armor 23"), Submission(24, null, "Test Armor 24"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.127");

            static IEnumerable<string> Handlers(string html) =>
                new[] { "Approve", "Reject", "TakeDown", "BackToPending" }.Where(handler => html.Contains($"?handler={handler}\"", StringComparison.Ordinal));

            var approved = await OpenAsync(admin, 22);
            Assert.Equal(["TakeDown", "BackToPending"], Handlers(approved.Html));
            Assert.Equal(ArmorStatus.Approved, approved.Expected);

            Assert.Equal(["Approve", "BackToPending"], Handlers((await OpenAsync(admin, 23)).Html));

            var none = await OpenAsync(admin, 24);
            Assert.Equal(["BackToPending"], Handlers(none.Html));
            Assert.Equal("", none.Expected);
            Assert.Contains("<dt>Status</dt> <dd> No status </dd>", Squeezed(none.Html));
        }

        // A status the table can't match to any of the website's words: the bot doesn't show the armor, only the All tab
        // lists it, and the dashboard can't change it, so there are no forms
        [Fact]
        public async Task StatusTheWebsiteDoesntUseCantBeChanged()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(25, "Pending"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.128");

            var response = await admin.Client.GetAsync("/admin/armor/25", Token);
            var html = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("?handler=", html);
            Assert.Contains(ArmorWords.UnknownStatus, WebUtility.HtmlDecode(html));
            Assert.Contains("(not one the website uses)", html);
            Assert.Contains("href=\"/admin/armor?status=all\">Back to the list</a>", html);

            Assert.DoesNotContain("href=\"/admin/armor/25", await admin.Client.GetStringAsync("/admin/armor?status=pending", Token));
            var all = await admin.Client.GetStringAsync("/admin/armor?status=all", Token);
            Assert.Contains("href=\"/admin/armor/25", all);
            Assert.Contains("(not one the website uses)", all);
        }

        // A status the table's collation can't tell from one of the website's words, such as 'approved ', is that word to
        // the bot, which shows the armor, and to the tabs, which list it. So it's that word here too, with its actions,
        // and the page says how it's stored. Fails if the page says the bot doesn't use the status, as it did.
        [Theory]
        [InlineData("approved")]
        [InlineData("Approved ")]
        [InlineData("APPROVED")]
        public async Task StatusSpeltDifferentlyIsTheWebsitesWord(string stored)
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(26, stored, "Test Armor 26"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.134");

            Assert.Contains("href=\"/admin/armor/26", await admin.Client.GetStringAsync("/admin/armor?status=approved", Token));

            var page = await OpenAsync(admin, 26);

            Assert.Equal(ArmorStatus.Approved, page.Expected);
            Assert.Contains(ArmorWords.SpeltDifferently(ArmorStatus.Approved), page.Text);
            Assert.Contains("<dd> Approved <span class=\"muted\">(stored as <bdi class=\"revealed\">", Regex.Replace(page.Html, @"\s+", " "));
            Assert.DoesNotContain(ArmorWords.UnknownStatus, page.Text);
            Assert.Contains("action=\"/admin/armor/26?handler=TakeDown\"", page.Html);
            Assert.Contains("action=\"/admin/armor/26?handler=BackToPending\"", page.Html);
            Assert.Contains("href=\"/admin/armor?status=approved\">Back to the list</a>", page.Html);
        }

        [Theory]
        [InlineData("/admin/armor/999")]
        [InlineData("/admin/armor/0")]
        [InlineData("/admin/armor/-5")]
        [InlineData("/admin/armor/abc")]
        [InlineData("/admin/armor/99999999999999999999")]
        [InlineData("/admin/armor/1.5")]
        public async Task ArmorThatIsntThereIsNotFound(string path)
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(1));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.129");

            var response = await admin.Client.GetAsync(path, Token);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("There's no page at that address.", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)));
        }

        // The done word is mapped to fixed text, shown only while the armor has the status that action gives, so a link
        // someone made, or a reload after someone else's change, can't say something happened that didn't. Anything else
        // in the address shows nothing.
        [Fact]
        public async Task MessageAfterAnActionIsFixedTextForTheStatusItGave()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(26), Submission(32, ArmorStatus.Approved, "Test Armor 32"));
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.130");

            var approved = WebUtility.HtmlDecode(await admin.Client.GetStringAsync("/admin/armor/32?done=approved", Token));
            Assert.Contains(ArmorWords.Approved, approved);

            foreach (var done in new[] { "approved", "rejected", "taken-down" })
                Assert.DoesNotContain("class=\"notice", await admin.Client.GetStringAsync("/admin/armor/26?done=" + done, Token));

            foreach (var done in new[] { "taken-down", "pending", "rejected" })
                Assert.DoesNotContain("class=\"notice", await admin.Client.GetStringAsync("/admin/armor/32?done=" + done, Token));

            var crafted = await admin.Client.GetStringAsync("/admin/armor/26?done=" + Uri.EscapeDataString("<b>owned</b>"), Token);
            Assert.DoesNotContain("owned", crafted);
            Assert.DoesNotContain("class=\"notice", crafted);
        }

        // The rules and the buttons come last, after the bot's messages and the history, so a reviewer reaches Approve
        // having passed an earlier rejection's reason. Each button names the paragraph that says what it does.
        [Fact]
        public async Task DecideComesAfterThePreviewsAndTheHistory()
        {
            var db = await fixture.ReadyAsync();
            await db.AddAsync(Submission(33, ArmorStatus.NotApproved, "Test Armor 33"));
            await db.RootExecuteAsync($"INSERT INTO {MariaDbFixture.ReviewTable} (armor_id, from_status, to_status, reason, reviewed_by, reviewer_name, reviewed_at) " +
                "VALUES (33, 'Pending Approval', 'Not Approved', 'Copied from another author', 1000000000000000002, 'Test Admin', UTC_TIMESTAMP(3))");
            await using var factory = new DashboardFactory(db);
            var admin = await ReviewerAsync(factory, "203.0.113.135");

            var html = (await OpenAsync(admin, 33)).Html;
            var order = new[] { "id=\"submission\"", "id=\"screenshot\"", "id=\"armor-style\"", "id=\"previews\"", "id=\"history\"", "id=\"rules\"", "id=\"decide\"" }
                .Select(section => html.IndexOf(section, StringComparison.Ordinal)).ToList();

            Assert.DoesNotContain(-1, order);
            Assert.Equal(order.Order(), order);
            Assert.True(html.IndexOf("Copied from another author", StringComparison.Ordinal) < html.IndexOf("?handler=Approve", StringComparison.Ordinal));
            Assert.Contains("<p class=\"muted\">Newest first. Changes made on the website aren't listed here.</p>", html);

            Assert.Contains("aria-describedby=\"approve-help\">Approve</button>", html);
            Assert.Contains("<p id=\"approve-help\" class=\"muted\">", html);
            Assert.Contains("aria-describedby=\"pending-help\">Back to pending</button>", html);
            Assert.Contains("<p id=\"pending-help\" class=\"muted\">", html);
        }

        // The embed's colour bar is the bot's, which BotMessages and the bot's own tests write out. Fails if the stylesheet's
        // bar drifts from it.
        [Fact]
        public async Task EmbedBarIsTheBotsColour()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var css = await factory.Visitor().Client.GetStringAsync("/css/site.css", Token);

            Assert.Contains($"border-left: 4px solid {BotMessages.Color};", Regex.Match(css, @"\n\.embed \{[^}]*\}").Value);
        }
    }
}
