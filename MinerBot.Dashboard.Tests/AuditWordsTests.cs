using System.Reflection;
using System.Text.RegularExpressions;
using DataAccessLibrary.Audit;
using MinerBot.Dashboard.Armor;
using MinerBot.Dashboard.Audit;
using MinerBot.Dashboard.Blacklist;

namespace MinerBot.Dashboard.Tests
{
    // One list of every action word the dashboard writes, which the audit page's filter offers, and how the pages show a
    // row's target and JSON. Fails for a word the code writes that isn't on the list, so the filter can always find it.
    public partial class AuditWordsTests
    {
        static readonly HashSet<string> Listed = [.. AuditWords.Actions.Select(action => action.Word)];

        // Each word one AddAudit takes, once, with a label of its own
        [Fact]
        public void EveryWordIsOneTheAuditLogTakes()
        {
            Assert.Equal(AuditWords.Actions.Count, Listed.Count);
            Assert.Equal(AuditWords.Actions.Count, AuditWords.Actions.Select(action => action.Label).Distinct().Count());
            Assert.All(AuditWords.Actions, action => Assert.Matches(@"^[a-z_]+(\.[a-z_]+)+\z", action.Word));
            Assert.All(AuditWords.Actions, action => Assert.InRange(action.Word.Length, 1, 50));
            Assert.All(AuditWords.Actions, action => Assert.False(string.IsNullOrWhiteSpace(action.Label)));
            Assert.All(AuditWords.Actions, action => Assert.Equal(action.Label, AuditWords.ActionLabel(action.Word)));

            // Written out, because rows already written use them and the filter matches them exactly
            Assert.Equal(["armor.approve", "armor.pending", "armor.reject", "armor.takedown", "auth.refused", "auth.signin", "auth.signout",
                "blacklist.add", "blacklist.edit", "blacklist.remove"], Listed.Order(StringComparer.Ordinal));
        }

        // Every word the code can write: each constant, and each action of the armor and blacklist pages
        [Fact]
        public void EveryWordTheCodeWritesIsOnTheList()
        {
            var constants = typeof(AuditWords).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!)
                .Where(value => value.Contains('.'))
                .ToList();

            Assert.Equal(Listed.Count, constants.Count);
            Assert.All(constants, word => Assert.Contains(word, Listed));
            Assert.All(Enum.GetValues<ArmorAction>(), action => Assert.Contains(action.AuditAction(), Listed));
            Assert.All(Enum.GetValues<BlacklistAction>(), action => Assert.Contains(action.AuditAction(), Listed));
        }

        // No other file of the dashboard writes an action word as a bare string, which the list could miss. The test's
        // build copies the dashboard's code next to it.
        [Fact]
        public void NoOtherFileWritesAnActionWord()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "dashboard-source");
            var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);

            Assert.Contains(files, file => Path.GetFileName(file) == "DbTicketStore.cs");
            Assert.Contains(files, file => Path.GetFileName(file) == "BlacklistChanges.cs");

            foreach (var file in files)
            {
                var words = ActionLiteral().Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value).ToList();

                if (Path.GetFileName(file) == "AuditWords.cs")
                    Assert.All(words.Where(word => word.Contains('.')), word => Assert.Contains(word, Listed));
                else
                    Assert.True(words.Count == 0, $"{Path.GetFileName(file)} writes {string.Join(", ", words)} as a bare string, which belongs in AuditWords");
            }
        }

        [Fact]
        public void OutcomesAndRolesHaveLabels()
        {
            Assert.All(AuditOutcome.All, outcome => Assert.NotEqual(outcome, AuditWords.OutcomeLabel(outcome)));
            Assert.All(AuditRole.All, role => Assert.NotEqual(role, AuditWords.RoleLabel(role)));
            Assert.Equal("Not sure it was saved", AuditWords.OutcomeLabel(AuditOutcome.Unknown));

            // A word from a later version is shown as it is, encoded by the page
            Assert.Equal("guild.settings.save", AuditWords.ActionLabel("guild.settings.save"));
            Assert.Equal("moderator", AuditWords.RoleLabel("moderator"));
        }

        // A link only from an ID that parsed as one the dashboard has a page for, made from the number, never the text
        [Theory]
        [InlineData("armor", "31", "Armor 31", "/admin/armor/31")]
        [InlineData("armor", "-31", "Armor -31", "/admin/armor/-31")]
        [InlineData("armor", "9223372036854775808", "Armor 9223372036854775808", null)]
        [InlineData("armor", "31abc", "Armor 31abc", null)]
        [InlineData("armor", " 31", "Armor  31", null)]
        [InlineData("blacklist", "12345", "Blacklist entry [U:1:12345]", "/admin/blacklist/12345")]
        [InlineData("blacklist", "0", "Blacklist entry [U:1:0]", "/admin/blacklist/0")]
        [InlineData("blacklist", "4294967295", "Blacklist entry [U:1:4294967295]", "/admin/blacklist/4294967295")]
        [InlineData("blacklist", "4294967296", "Blacklist entry 4294967296", null)]
        [InlineData("blacklist", "-1", "Blacklist entry -1", null)]
        [InlineData("blacklist", "+12345", "Blacklist entry +12345", null)]
        [InlineData("blacklist", "0012345", "Blacklist entry [U:1:12345]", "/admin/blacklist/12345")]
        [InlineData("session", null, "Session", null)]
        [InlineData("guild", "5", "guild 5", null)]
        [InlineData(null, "5", "5", null)]
        public void TargetLinksOnlyFromAParsedId(string? type, string? id, string text, string? link)
        {
            Assert.Equal(new AuditTarget(text, link), AuditRows.Target(type, id));
        }

        [Fact]
        public void RowWithNoTargetHasNone()
        {
            Assert.Null(AuditRows.Target(null, null));
        }

        // JSON is parsed into labelled fields; anything that isn't an object is its text, cut short
        [Fact]
        public void JsonIsReadIntoFields()
        {
            Assert.Null(AuditRows.Change(null));

            var change = AuditRows.Change("""{"Name":"Test Player","Reason":null,"Expires":"2027-01-02 00:00:00","Notes":"","Ticked":["NameTaken:Name","Nothing:Name",5],"Count":5}""");
            Assert.NotNull(change);
            Assert.Null(change.Text);
            Assert.Equal(
            [
                new AuditField("Name", "Test Player"),
                new AuditField("Reason", null),
                new AuditField("End date", "2027-01-02 00:00:00"),
                new AuditField("Notes", ""),
                new AuditField("Warnings ticked", "Name already approved (name), Nothing:Name, 5"),
                new AuditField("Count", "5")
            ], change.Fields);

            var empty = AuditRows.Change("{}");
            Assert.NotNull(empty);
            Assert.Empty(empty.Fields);
            Assert.Null(empty.Text);

            Assert.Equal("[1,2]", AuditRows.Change("[1,2]")?.Text);
            Assert.Equal("\"text\"", AuditRows.Change("\"text\"")?.Text);
            Assert.Equal("{\"Name\":", AuditRows.Change("{\"Name\":")?.Text);
            Assert.Equal("not json", AuditRows.Change("not json")?.Text);

            var longValue = AuditRows.Change($$"""{"Notes":"{{new string('n', 400)}}"}""");
            Assert.Equal(new string('n', AuditRows.ValueLimit) + "…", longValue?.Fields[0].Value);

            // Never half of a character outside the Basic Multilingual Plane
            var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));
            Assert.Equal(emoji[..AuditRows.ValueLimit] + "…", AuditRows.Cut(emoji));
            Assert.Equal(new string('x', 299) + "…", AuditRows.Cut(new string('x', 299) + "\U0001F600" + "rest"));
        }

        [GeneratedRegex("\"([a-z_]+(?:\\.[a-z_]+)+)\"")]
        private static partial Regex ActionLiteral();
    }
}
