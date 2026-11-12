using DataAccessLibrary.Armor.Models;
using MinerBot.Dashboard.Armor;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Tests
{
    // What the review forms may send, which actions each status offers, and the version that tells whether an armor
    // changed. All of it is checked before the database, which throws for anything it can't save.
    public class ArmorFormTests
    {
        [Theory]
        [InlineData(null, true, null)]
        [InlineData("", true, null)]
        [InlineData("Pending Approval", true, "Pending Approval")]
        [InlineData("Approved", true, "Approved")]
        [InlineData("Not Approved", true, "Not Approved")]
        [InlineData("approved", false, "approved")]
        [InlineData("Pending", false, "Pending")]
        [InlineData("Approved ", false, "Approved ")]
        public void ExpectedStatusIsTheWebsitesWordOrNone(string? value, bool known, string? expected)
        {
            Assert.Equal(known, ArmorForm.TryExpected(value, out var status));
            Assert.Equal(expected, status);
        }

        // Browsers send a line end in a box as two characters, which the box counted as one
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("  \r\n ", null)]
        [InlineData(" Not real ", "Not real")]
        [InlineData("Not\r\nreal\rat all", "Not\nreal\nat all")]
        public void ReasonIsTrimmedAndItsLineEndsMadeOne(string? value, string? reason)
        {
            Assert.True(ArmorForm.TryReason(value, out var kept));
            Assert.Equal(reason, kept);
        }

        // armor_reviews.reason holds 500 code points, so an emoji counts once, and anything longer only comes from a form
        // someone made up
        [Fact]
        public void ReasonOverTheColumnsLimitIsRefused()
        {
            Assert.True(ArmorForm.TryReason(new string('a', 500), out _));
            Assert.False(ArmorForm.TryReason(new string('a', 501), out _));
            Assert.True(ArmorForm.TryReason(string.Concat(Enumerable.Repeat("\U0001F600", 500)), out _));
            Assert.False(ArmorForm.TryReason(string.Concat(Enumerable.Repeat("\U0001F600", 501)), out _));
            Assert.True(ArmorForm.TryReason(string.Concat(Enumerable.Repeat("a\r\n", 250)), out var lines));
            Assert.Equal(499, lines!.Length);
        }

        [Fact]
        public void TicksAreKindsAndFieldsByTheirNames()
        {
            Assert.True(ArmorForm.TryTicks(null, out var none));
            Assert.Empty(none);

            Assert.True(ArmorForm.TryTicks(["HiddenCharacters:Name", "LinkSyntax:Description", "NoScreenshot:Screenshot", "HiddenCharacters:Helmet", "LinkSyntax:Skin"], out var ticks));
            Assert.Equal(["HiddenCharacters:Helmet", "HiddenCharacters:Name", "LinkSyntax:Description", "LinkSyntax:Skin", "NoScreenshot:Screenshot"], ticks.Order(StringComparer.Ordinal));

            foreach (var made in new[] { "Hidden:Name", "HiddenCharacters:Nope", "HiddenCharacters", "HiddenCharacters:Name:Name", "13:1", "hiddencharacters:name", "" })
                Assert.False(ArmorForm.TryTicks([made], out _), made);

            Assert.False(ArmorForm.TryTicks([null!], out _));
            Assert.True(ArmorForm.TryTicks([.. Enumerable.Repeat("LinkSyntax:Author", ArmorForm.MaxTicks)], out _));
            Assert.False(ArmorForm.TryTicks([.. Enumerable.Repeat("LinkSyntax:Author", ArmorForm.MaxTicks + 1)], out _));
        }

        // Every warning a form can show fits under the limit on ticks
        [Fact]
        public void EveryWarningAtOnceFitsTheTicksLimit()
        {
            var armor = new ArmorSubmission
            {
                ID = 1,
                ArmorName = "Night​Raid‮ @here https://example.com",
                ArmorDescription = "Ping ​‮ @everyone at https://example.com ```",
                Author = "Test​‮ @here www.example.com `",
                ArmorChoice = "Tri​Clops‮ @here https://example.com",
                ArmorSkin = "Gold​‮ @everyone www.example.com"
            };

            var warnings = ArmorRules.Check(armor, nameTaken: false).Warnings;

            Assert.Equal(23, warnings.Count);
            Assert.True(ArmorForm.TryTicks([.. warnings.Select(ArmorForm.TickKey)], out _));
        }

        // Written out, because the audit page filters on them
        [Fact]
        public void AuditWordsAreFixed()
        {
            Assert.Equal("armor.approve", ArmorAction.Approve.AuditAction());
            Assert.Equal("armor.reject", ArmorAction.Reject.AuditAction());
            Assert.Equal("armor.takedown", ArmorAction.TakeDown.AuditAction());
            Assert.Equal("armor.pending", ArmorAction.BackToPending.AuditAction());
        }

        // A status the website doesn't use offers nothing, since the data layer refuses to change it
        [Fact]
        public void EachStatusOffersItsOwnActions()
        {
            Assert.Equal([ArmorAction.Approve, ArmorAction.Reject], ArmorActions.From(ArmorStatus.Pending));
            Assert.Equal([ArmorAction.TakeDown, ArmorAction.BackToPending], ArmorActions.From(ArmorStatus.Approved));
            Assert.Equal([ArmorAction.Approve, ArmorAction.BackToPending], ArmorActions.From(ArmorStatus.NotApproved));
            Assert.Equal([ArmorAction.BackToPending], ArmorActions.From(null));
            Assert.Empty(ArmorActions.From("approved"));

            // The status they start from is the word the table reads the armor's as, so armor stored as 'approved ' is
            // Approved, and one stored as a word the website doesn't use has none, which differs from having no status
            Assert.True(ArmorActions.TryStatusOf(new ArmorSubmission { Status = "approved ", KnownStatus = ArmorStatus.Approved }, out var spelt));
            Assert.Equal(ArmorStatus.Approved, spelt);
            Assert.True(ArmorActions.TryStatusOf(new ArmorSubmission { Status = null }, out var none));
            Assert.Null(none);
            Assert.False(ArmorActions.TryStatusOf(new ArmorSubmission { Status = "Pending" }, out _));

            Assert.Equal(ArmorStatus.Approved, ArmorAction.Approve.Target());
            Assert.Equal(ArmorStatus.NotApproved, ArmorAction.Reject.Target());
            Assert.Equal(ArmorStatus.NotApproved, ArmorAction.TakeDown.Target());
            Assert.Equal(ArmorStatus.Pending, ArmorAction.BackToPending.Target());

            // No action leads to the status it starts from, which ChangeStatus would refuse
            Assert.All(Enum.GetValues<ArmorAction>(), action => Assert.False(action.StartsFrom(action.Target())));
        }

        [Fact]
        public void DoneWordsComeBackAsTheirActions()
        {
            Assert.All(Enum.GetValues<ArmorAction>(), action => Assert.Equal(action, ArmorActions.FromDoneWord(action.DoneWord())));
            Assert.Null(ArmorActions.FromDoneWord("<b>approved</b>"));
            Assert.Null(ArmorActions.FromDoneWord(null));
        }

        [Theory]
        [InlineData(null, "pending", ArmorStatus.Pending)]
        [InlineData("", "pending", ArmorStatus.Pending)]
        [InlineData("pending", "pending", ArmorStatus.Pending)]
        [InlineData("approved", "approved", ArmorStatus.Approved)]
        [InlineData("NOT-APPROVED", "not-approved", ArmorStatus.NotApproved)]
        [InlineData("all", "all", null)]
        public void TabWordsMapToTheWebsitesStatuses(string? word, string tab, string? status)
        {
            var found = ArmorTabs.FromWord(word);

            Assert.Equal(tab, found?.Word);
            Assert.Equal(status, found?.Status);
        }

        // The website's own words aren't the tabs' words
        [Theory]
        [InlineData("Pending Approval")]
        [InlineData("not approved")]
        [InlineData("everything")]
        public void OtherTabWordsAreNone(string word)
        {
            Assert.Null(ArmorTabs.FromWord(word));
        }

        static ArmorSubmission Armor() => new()
        {
            ID = 5,
            CreatedAt = new DateTime(2026, 11, 1, 12, 0, 0),
            Status = ArmorStatus.Pending,
            Author = "Test Author",
            ArmorName = "Test Armor 5",
            ArmorDescription = "A dark set.",
            ArmorScreenshot = "https://murderminershub.com/wp-content/uploads/x.png",
            ArmorChoice = "Bug",
            ArmorSkin = "Default",
            Helmet = 1, Visor = 2, Torso = 3, Shoulders = 4, Biceps = 5, Forearms = 6, Hands = 7,
            Pelvis = 8, Thighs = 9, Legs = 10, Boots = 11, Shields = 12, Other = 13
        };

        [Fact]
        public void VersionIsTheSameForTheSameArmor()
        {
            var version = ArmorVersion.Of(Armor());

            Assert.Equal(version, ArmorVersion.Of(Armor()));
            Assert.True(ArmorVersion.IsWellFormed(version));
            Assert.Matches("^[0-9a-f]{64}$", version);
        }

        // Each thing the page shows changes the version. Fails for a field the version leaves out, whose change would then
        // go through unseen.
        [Theory]
        [InlineData("ArmorName", "Test Armor 6")]
        [InlineData("ArmorDescription", "A light set.")]
        [InlineData("Author", "Other Author")]
        [InlineData("ArmorScreenshot", "https://murderminershub.com/wp-content/uploads/y.png")]
        [InlineData("ArmorChoice", "Tri-Clops")]
        [InlineData("ArmorSkin", "Gold")]
        [InlineData("ArmorDescription", null)]
        [InlineData("ColorArray", "AAAAAAAAAAAAAAAAAA==")]
        public void ChangingWhatThePageShowsChangesTheVersion(string field, string? value)
        {
            var armor = Armor();
            typeof(ArmorSubmission).GetProperty(field)!.SetValue(armor, value);

            Assert.NotEqual(ArmorVersion.Of(Armor()), ArmorVersion.Of(armor));
        }

        [Theory]
        [InlineData("Helmet")]
        [InlineData("Visor")]
        [InlineData("Torso")]
        [InlineData("Shoulders")]
        [InlineData("Biceps")]
        [InlineData("Forearms")]
        [InlineData("Hands")]
        [InlineData("Pelvis")]
        [InlineData("Thighs")]
        [InlineData("Legs")]
        [InlineData("Boots")]
        [InlineData("Shields")]
        [InlineData("Other")]
        public void ChangingAColourChangesTheVersion(string part)
        {
            var armor = Armor();
            typeof(ArmorSubmission).GetProperty(part)!.SetValue(armor, 200);

            Assert.NotEqual(ArmorVersion.Of(Armor()), ArmorVersion.Of(armor));

            // A colour the website left empty reads as 0 to the bot, but the page shows it as empty, so that counts too
            typeof(ArmorSubmission).GetProperty(part)!.SetValue(armor, null);
            var zero = Armor();
            typeof(ArmorSubmission).GetProperty(part)!.SetValue(zero, 0);
            Assert.NotEqual(ArmorVersion.Of(zero), ArmorVersion.Of(armor));
        }

        // The bot's hourly job stores the save string the page already shows for approved armor, which isn't a change. The
        // status is checked on its own, and the page shows neither the user ID nor the time as anything to review.
        [Fact]
        public void WhatThePageDoesntReviewLeavesTheVersion()
        {
            var armor = Armor();
            armor.ColorArray = "AQIDBAUGBwgJCgsMDQ==";
            armor.Status = ArmorStatus.Approved;
            armor.UserID = 1000000000000000001;
            armor.CreatedAt = armor.CreatedAt.AddDays(1);

            Assert.Equal(ArmorVersion.Of(Armor()), ArmorVersion.Of(armor));
        }

        // Two rows whose text would run together the same way without the lengths
        [Fact]
        public void FieldsDontRunTogether()
        {
            var first = Armor();
            first.ArmorName = "ab";
            first.ArmorDescription = "c";

            var second = Armor();
            second.ArmorName = "a";
            second.ArmorDescription = "bc";

            Assert.NotEqual(ArmorVersion.Of(first), ArmorVersion.Of(second));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeF")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefa")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
        public void VersionThatOfCantMakeIsRefused(string? version)
        {
            Assert.False(ArmorVersion.IsWellFormed(version));
        }
    }
}
