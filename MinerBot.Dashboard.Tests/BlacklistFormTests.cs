using DataAccessLibrary.Blacklist;
using DataAccessLibrary.Blacklist.Models;
using MinerBot.Dashboard.Blacklist;
using MinerBot.Shared.Steam;

namespace MinerBot.Dashboard.Tests
{
    // What the blacklist's forms may send, the ways the pages write an account, the version that tells whether an entry
    // changed, and the audit log's JSON for it. All of it is checked before the database, which throws for anything it
    // can't save.
    public class BlacklistFormTests
    {
        const string Emoji = "\U0001F600";

        static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

        // Trimmed, empty is none, and counted as the columns count, so an emoji is one character
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(" Test Player ", "Test Player")]
        [InlineData("Night\U0000200BRaid", "Night\U0000200BRaid")]
        public void TextIsTrimmedAndEmptyIsNone(string? value, string? text)
        {
            Assert.Equal(TextProblem.None, BlacklistForm.CheckText(value, BlacklistData.UsernameLimit, out var kept));
            Assert.Equal(text, kept);
        }

        [Fact]
        public void TextPastTheLimitIsRefused()
        {
            Assert.Equal(TextProblem.None, BlacklistForm.CheckText(new string('a', 60), 60, out _));
            Assert.Equal(TextProblem.TooLong, BlacklistForm.CheckText(new string('a', 61), 60, out _));
            Assert.Equal(TextProblem.None, BlacklistForm.CheckText(Repeat(Emoji, 60), 60, out _));
            Assert.Equal(TextProblem.TooLong, BlacklistForm.CheckText(Repeat(Emoji, 61), 60, out _));
            Assert.Equal(TextProblem.None, BlacklistForm.CheckText("  " + new string('a', 60) + "  ", 60, out _));
        }

        // A one-line box can't send them, and the website would show them as they are
        [Theory]
        [InlineData("Line\nbreak")]
        [InlineData("Line\r\nbreak")]
        [InlineData("Tab\tinside")]
        [InlineData("Nul\0inside")]
        [InlineData("Escape\u001binside")]
        [InlineData("Next line\u0085inside")]
        public void ControlCharactersAreRefused(string value)
        {
            Assert.Equal(TextProblem.ControlCharacter, BlacklistForm.CheckText(value, 120, out _));
        }

        // A day as the date box sends it, stored at 00:00 that day, in the years the box allows. Past days are allowed.
        [Theory]
        [InlineData(null, true, null)]
        [InlineData("", true, null)]
        [InlineData("2027-01-02", true, "2027-01-02 00:00:00")]
        [InlineData("2000-01-01", true, "2000-01-01 00:00:00")]
        [InlineData("9998-12-31", true, "9998-12-31 00:00:00")]
        [InlineData("2001-02-03", true, "2001-02-03 00:00:00")]
        [InlineData("1999-12-31", false, null)]
        [InlineData("9999-01-01", false, null)]
        [InlineData("2027-02-30", false, null)]
        [InlineData("2027-13-01", false, null)]
        [InlineData("2027-1-2", false, null)]
        [InlineData("02/01/2027", false, null)]
        [InlineData(" 2027-01-02", false, null)]
        [InlineData("2027-01-02T00:00", false, null)]
        [InlineData("tomorrow", false, null)]
        public void EndIsADayInTheBoxesYears(string? value, bool passes, string? expires)
        {
            Assert.Equal(passes, BlacklistForm.TryExpires(value, out var day));
            Assert.Equal(expires, day?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(day == null || day.Value.Kind == DateTimeKind.Unspecified);
        }

        // The stored end's own day passes whatever its year, so an entry written elsewhere can still be saved with it
        [Fact]
        public void StoredEndsOwnDayPassesOutsideTheYears()
        {
            var stored = new DateTime(9999, 12, 31, 23, 59, 59);

            Assert.True(BlacklistForm.TryExpires("9999-12-31", out var day, stored));
            Assert.Equal(new DateTime(9999, 12, 31), day);
            Assert.False(BlacklistForm.TryExpires("9999-12-30", out _, stored));
            Assert.True(BlacklistForm.TryExpires("1970-01-01", out _, new DateTime(1970, 1, 1, 0, 0, 1)));
            Assert.False(BlacklistForm.TryExpires("1970-01-01", out _, null));
        }

        // A whole number the column can hold that no account number can be, read without throwing
        [Theory]
        [InlineData("5000000000", true)]
        [InlineData("4294967296", true)]
        [InlineData("-1", true)]
        [InlineData("-9999999999", true)]
        [InlineData("9999999999", true)]
        [InlineData("+5000000000", true)]
        [InlineData("4294967295", false)]
        [InlineData("0", false)]
        [InlineData("-0", false)]
        [InlineData("12345", false)]
        [InlineData("10000000000", false)]
        [InlineData("5000000000.0", false)]
        [InlineData("5000000000.5", false)]
        [InlineData("5e9", false)]
        [InlineData(" 5000000000", false)]
        [InlineData("5,000,000,000", false)]
        [InlineData("79228162514264337593543950336", false)]
        [InlineData("abc", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void NumberOfARowWithNoAccountIsChecked(string? value, bool passes)
        {
            Assert.Equal(passes, BlacklistForm.TryNumber(value, out _));
        }

        [Fact]
        public void NumbersAreWrittenAsDigitsAlone()
        {
            Assert.Equal("5000000000", BlacklistForm.NumberText(5000000000m));
            Assert.Equal("5000000000", BlacklistForm.NumberText(5000000000.0m));
            Assert.Equal("-1", BlacklistForm.NumberText(-1m));
        }

        // Every way the pages write an account, and each reads back as that account. Account 0 has no SteamID64 or
        // profile, since ToSteamId64 throws for it.
        [Theory]
        [InlineData(12345u, "76561197960278073", "[U:1:12345]", "STEAM_0:1:6172")]
        [InlineData(1u, "76561197960265729", "[U:1:1]", "STEAM_0:1:0")]
        [InlineData(2u, "76561197960265730", "[U:1:2]", "STEAM_0:0:1")]
        [InlineData(4294967295u, "76561202255233023", "[U:1:4294967295]", "STEAM_0:1:2147483647")]
        public void AccountIsWrittenEveryWay(uint account, string steamId64, string steam3, string steam2)
        {
            Assert.Equal(steamId64, SteamAccounts.SteamId64(account));
            Assert.Equal(steam3, SteamAccounts.Steam3(account));
            Assert.Equal(steam2, SteamAccounts.Steam2(account));
            Assert.Equal("https://steamcommunity.com/profiles/" + steamId64, SteamAccounts.ProfileLink(account));
            Assert.Equal(steamId64, SteamAccounts.Shown(account));
            Assert.Equal($"/admin/blacklist/{account}", SteamAccounts.EntryLink(account));

            foreach (var written in new[] { steamId64, steam3, steam2, SteamAccounts.ProfileLink(account) })
                Assert.Equal(new SteamIdResult(SteamIdKind.AccountNumber, account, null, null), SteamIds.Parse(written));
        }

        [Fact]
        public void AccountZeroHasNoSteamId64()
        {
            Assert.Null(SteamAccounts.SteamId64(0));
            Assert.Null(SteamAccounts.ProfileLink(0));
            Assert.Equal("[U:1:0] (not a Steam account)", SteamAccounts.Shown(0));
            Assert.Equal("[U:1:0]", SteamAccounts.Steam3(0));
            Assert.Equal("0", SteamAccounts.TargetId(0));
        }

        [Fact]
        public void DoneWordsComeBackAsTheirActions()
        {
            Assert.All(Enum.GetValues<BlacklistAction>(), action => Assert.Equal(action, BlacklistActions.FromDoneWord(action.DoneWord())));
            Assert.Null(BlacklistActions.FromDoneWord("<b>added</b>"));
            Assert.Null(BlacklistActions.FromDoneWord("Added"));
            Assert.Null(BlacklistActions.FromDoneWord(null));
            Assert.Equal(["blacklist.add", "blacklist.edit", "blacklist.remove"], Enum.GetValues<BlacklistAction>().Select(action => action.AuditAction()));
        }

        static BlacklistModel Entry() => new()
        {
            steam_id = 12345,
            username = "Test Player",
            reason = "Testing",
            expire_date = new DateTime(2027, 1, 2, 3, 4, 5),
            notes = "Made up"
        };

        [Fact]
        public void VersionIsTheSameForTheSameEntry()
        {
            var version = BlacklistVersion.Of(Entry());

            Assert.Equal(version, BlacklistVersion.Of(Entry()));
            Assert.True(BlacklistVersion.IsWellFormed(version));
            Assert.Matches("^[0-9a-f]{64}$", version);

            // The account isn't part of it: the page's address holds that
            var other = Entry();
            other.steam_id = 54321;
            Assert.Equal(version, BlacklistVersion.Of(other));
        }

        // Each thing the page shows changes the version, null apart from empty, and an end a second apart. Fails for a field
        // the version leaves out, whose change would then go through unseen.
        [Theory]
        [InlineData("username", "Renamed Player")]
        [InlineData("username", "")]
        [InlineData("username", null)]
        [InlineData("reason", "")]
        [InlineData("reason", null)]
        [InlineData("notes", "")]
        [InlineData("notes", null)]
        [InlineData("expire_date", null)]
        [InlineData("expire_date", "second")]
        [InlineData("expire_date_unreadable", null)]
        public void ChangingWhatThePageShowsChangesTheVersion(string field, string? value)
        {
            var entry = Entry();

            switch (field)
            {
                case "expire_date": entry.expire_date = value == null ? null : entry.expire_date!.Value.AddSeconds(1); break;
                case "expire_date_unreadable": entry.expire_date = null; entry.expire_date_unreadable = true; break;
                default: typeof(BlacklistModel).GetProperty(field)!.SetValue(entry, value); break;
            }

            Assert.NotEqual(BlacklistVersion.Of(Entry()), BlacklistVersion.Of(entry));
        }

        // The flag alone changes it: an entry with no end, and the same entry once the website has stored a zero date, which
        // reads back as no end too. Otherwise a save from the page that showed no end would replace the zero date unseen.
        [Fact]
        public void UnreadableFlagAloneChangesTheVersion()
        {
            var noEnd = Entry();
            noEnd.expire_date = null;

            var unreadable = Entry();
            unreadable.expire_date = null;
            unreadable.expire_date_unreadable = true;

            Assert.NotEqual(BlacklistVersion.Of(noEnd), BlacklistVersion.Of(unreadable));
        }

        // Null and empty aren't the same, and fields don't run together into the same text
        [Fact]
        public void FieldsDontRunTogether()
        {
            var first = Entry();
            first.username = "ab";
            first.reason = "c";

            var second = Entry();
            second.username = "a";
            second.reason = "bc";

            Assert.NotEqual(BlacklistVersion.Of(first), BlacklistVersion.Of(second));

            var empty = Entry();
            empty.notes = "";
            var none = Entry();
            none.notes = null;
            Assert.NotEqual(BlacklistVersion.Of(empty), BlacklistVersion.Of(none));

            // An out-of-range row's version is worked out the same way
            Assert.Equal(BlacklistVersion.Of(Entry()), BlacklistVersion.Of(new OutOfRangeBan
            {
                steam_id = 5000000000,
                username = "Test Player",
                reason = "Testing",
                expire_date = new DateTime(2027, 1, 2, 3, 4, 5),
                notes = "Made up"
            }));
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
            Assert.False(BlacklistVersion.IsWellFormed(version));
        }

        // The box holds days, so a day that is the stored end's own keeps its time; another day is 00:00 that day; an empty
        // box is no end; and an end that wasn't a real date is replaced whatever the box holds
        [Fact]
        public void EditKeepsTheStoredEndOnItsOwnDay()
        {
            var entry = Entry();

            Assert.Equal(new DateTime(2027, 1, 2, 3, 4, 5), BlacklistChanges.Edited(entry, new BlacklistFields("Test Player", "Testing", new DateTime(2027, 1, 2), "Made up")).expire_date);
            Assert.Equal(new DateTime(2027, 1, 3), BlacklistChanges.Edited(entry, new BlacklistFields("Test Player", "Testing", new DateTime(2027, 1, 3), "Made up")).expire_date);
            Assert.Null(BlacklistChanges.Edited(entry, new BlacklistFields("Test Player", "Testing", null, "Made up")).expire_date);

            Assert.True(BlacklistChanges.Has(entry, new BlacklistFields("Test Player", "Testing", new DateTime(2027, 1, 2), "Made up")));
            Assert.False(BlacklistChanges.Has(entry, new BlacklistFields("Test Player", "Testing", new DateTime(2027, 1, 2), null)));

            var unreadable = Entry();
            unreadable.expire_date = null;
            unreadable.expire_date_unreadable = true;
            Assert.False(BlacklistChanges.Has(unreadable, new BlacklistFields("Test Player", "Testing", null, "Made up")));
            Assert.False(BlacklistChanges.Edited(unreadable, new BlacklistFields("Test Player", "Testing", null, "Made up")).expire_date_unreadable);
        }

        // A stored text as the form makes it from its box: without line breaks, which a text box drops, with NUL and the C1
        // controls read as the HTML parser reads their references, then trimmed, and empty as none. The mapping comes before
        // the trim, which would otherwise take a U+0085 the box sends as U+2026 off the end.
        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("  ", null)]
        [InlineData("\r\n", null)]
        [InlineData("Spaced Player ", "Spaced Player")]
        [InlineData("line one\nline two", "line oneline two")]
        [InlineData("line one\r\nline two", "line oneline two")]
        [InlineData("first\tsecond", "first\tsecond")]
        [InlineData("a\0b", "a\uFFFDb")]
        [InlineData("ends with NEL\u0085", "ends with NEL\u2026")]
        [InlineData("x\u0081y", "x\u0081y")]
        public void StoredTextIsComparedAsItsBoxSendsIt(string? stored, string? sent)
        {
            Assert.Equal(sent, BlacklistForm.AsSent(stored));
            Assert.True(BlacklistForm.IsAsStored(stored, sent));
            Assert.False(BlacklistForm.IsAsStored(stored, "Other text"));
        }

        // The HTML Standard's table for a numeric reference in an attribute, written out here rather than worked out, since
        // BlacklistSteps uses AsParsed to read a page's boxes as a browser does: NUL is the replacement character, the
        // other C0 controls and DEL stay, and 27 of the C1 controls are the Windows-1252 characters with their codes
        [Fact]
        public void ReferencesAreReadAsTheHtmlParserReadsThem()
        {
            const string c0 = "\uFFFD\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F"
                + "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F";
            const string c1 = "\u007F\u20AC\u0081\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u008D\u017D"
                + "\u008F\u0090\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u009D\u017E\u0178";

            Assert.Equal(c0, string.Concat(Enumerable.Range(0x00, 0x20).Select(code => BlacklistForm.AsParsed((char)code))));
            Assert.Equal(c1, string.Concat(Enumerable.Range(0x7F, 0x21).Select(code => BlacklistForm.AsParsed((char)code))));

            // Everything else comes back as it is, such as a space, a no-break space, a non-character and half an emoji
            foreach (var c in " a~\u00A0\u00FF\u2028\uFFFD\uFFFE\uD83D\uDE00")
                Assert.Equal(c, BlacklistForm.AsParsed(c));

            // A box's value: every character read so, and no line breaks, with the spaces at the ends kept
            Assert.Equal(" a\uFFFDb\u2026c\tz ", BlacklistForm.AsBox(" a\0b\u0085c\r\n\tz "));
            Assert.Null(BlacklistForm.AsBox(null));
        }

        // A box as it was sent is the entry's own text only when it's exactly what the page's box sends back. An empty box,
        // or none, is the entry's own when the box showed nothing. A space taken off either end is a change, though it trims
        // to the same text, and so is clearing a box that showed only spaces or another blank character.
        [Theory]
        [InlineData(" Bob", " Bob", true)]
        [InlineData(" Bob", "Bob", false)]
        [InlineData("Spaced Player ", "Spaced Player ", true)]
        [InlineData("Spaced Player ", "Spaced Player", false)]
        [InlineData("Bob", " Bob ", false)]
        [InlineData("", null, true)]
        [InlineData("", "", true)]
        [InlineData(null, "", true)]
        [InlineData(null, "   ", false)]
        [InlineData("   ", "   ", true)]
        [InlineData("   ", "", false)]
        [InlineData("   ", null, false)]
        [InlineData("\u000B", "\u000B", true)]
        [InlineData("\u000B", "", false)]
        [InlineData(" \t ", "", false)]
        [InlineData(null, null, true)]
        [InlineData("\r\n", null, true)]
        [InlineData("\r\n", "", true)]
        [InlineData("Bob", null, false)]
        [InlineData(null, "Bob", false)]
        [InlineData("line one\r\nline two", "line oneline two", true)]
        [InlineData("a\0b\u0085c\tz", "a\uFFFDb\u2026c\tz", true)]
        [InlineData("a\0b\u0085c\tz", "a\0b\u0085c\tz", false)]
        [InlineData("x\u0085", "x\u2026", true)]
        [InlineData("\u0085", null, false)]
        public void UntouchedBoxIsExactlyWhatThePageFilledIn(string? stored, string? box, bool untouched)
        {
            Assert.Equal(untouched, BlacklistForm.IsUntouched(stored, box));
        }

        // An edit keeps each text its box sent back as the entry holds it, so nothing the editor didn't change is rewritten,
        // and a tab that's the entry's own isn't the editor's to take out. One the editor typed is refused.
        [Fact]
        public void EditKeepsTextTheBoxCantShow()
        {
            var entry = Entry();
            entry.username = "Spaced Player ";
            entry.reason = "";
            entry.notes = "line one\nline two\twith a tab";

            Assert.True(BlacklistForm.TryFields("Spaced Player ", "", "2027-01-02", "line oneline two\twith a tab", entry, out var fields, out _));
            Assert.True(BlacklistChanges.Has(entry, fields));

            var renamed = BlacklistChanges.Edited(entry, fields with { Name = "Renamed Player", Boxes = fields.Boxes! with { Name = "Renamed Player" } });
            Assert.Equal(("Renamed Player", "", "line one\nline two\twith a tab"), (renamed.username, renamed.reason, renamed.notes));
            Assert.Equal(("""{"Name":"Spaced Player "}""", """{"Name":"Renamed Player"}"""), BlacklistJson.Changed(entry, renamed));

            Assert.False(BlacklistForm.TryFields("Spaced Player", "", "2027-01-02", "line one\tchanged", entry, out _, out var problems));
            Assert.Equal([BlacklistField.Notes], problems.Keys);

            // Without the entry, as for an add, a tab is always refused
            Assert.False(BlacklistForm.TryFields("Spaced Player", "", "2027-01-02", "line oneline two\twith a tab", null, out _, out _));
        }

        // A box left alone and one the editor took a space off the ends of trim to the same text, and only the boxes as
        // they were sent tell them apart: the first keeps the stored text, and the second saves the trimmed one
        [Fact]
        public void SpaceTakenOffTheEndsIsAChange()
        {
            var entry = Entry();
            entry.username = " Bob";
            entry.reason = "Spaced reason ";

            Assert.True(BlacklistForm.TryFields(" Bob", "Spaced reason ", "2027-01-02", "Made up", entry, out var untouched, out _));
            Assert.True(BlacklistChanges.Has(entry, untouched));
            Assert.Equal((" Bob", "Spaced reason "), (BlacklistChanges.Edited(entry, untouched).username, BlacklistChanges.Edited(entry, untouched).reason));

            Assert.True(BlacklistForm.TryFields("Bob", "Spaced reason", "2027-01-02", "Made up", entry, out var trimmed, out _));
            var edited = BlacklistChanges.Edited(entry, trimmed);
            Assert.Equal(("Bob", "Spaced reason"), (edited.username, edited.reason));
            Assert.Equal(("""{"Name":" Bob","Reason":"Spaced reason "}""", """{"Name":"Bob","Reason":"Spaced reason"}"""), BlacklistJson.Changed(entry, edited));
            Assert.False(BlacklistChanges.Has(entry, trimmed));

            // A tab that's the entry's own passes only in a box left alone, so taking a space off its end makes it the
            // editor's to take out
            entry.notes = "Pasted\tnote ";
            Assert.True(BlacklistForm.TryFields(" Bob", "Spaced reason ", "2027-01-02", "Pasted\tnote ", entry, out _, out _));
            Assert.False(BlacklistForm.TryFields(" Bob", "Spaced reason ", "2027-01-02", "Pasted\tnote", entry, out _, out var problems));
            Assert.Equal([BlacklistField.Notes], problems.Keys);
        }

        // A text the form would refuse is written only as the entry's own, unchanged. The form lets one through as the entry
        // held it when the handler read it, so a locked row that no longer holds it is a change made in between.
        [Fact]
        public void TextTheFormRefusesIsWrittenOnlyAsTheEntrysOwn()
        {
            var tabbed = Entry();
            tabbed.notes = "Pasted\tnote";

            Assert.True(BlacklistForm.Writable(Entry(), Entry()));
            Assert.True(BlacklistForm.Writable(tabbed, tabbed));
            Assert.False(BlacklistForm.Writable(Entry(), tabbed));
        }

        // Likewise an end outside the date box's years, which the form lets through only as the entry's own, is written
        // only while it's exactly that
        [Fact]
        public void EndOutsideTheYearsIsWrittenOnlyAsTheEntrysOwn()
        {
            static BlacklistModel Ending(DateTime? end)
            {
                var entry = Entry();
                entry.expire_date = end;
                return entry;
            }

            var far = Ending(new DateTime(9999, 12, 31, 23, 59, 59));

            Assert.True(BlacklistForm.Writable(far, far));
            Assert.True(BlacklistForm.Writable(far, Ending(new DateTime(2031, 1, 1))));
            Assert.True(BlacklistForm.Writable(far, Ending(null)));
            Assert.True(BlacklistForm.Writable(Entry(), Ending(new DateTime(2000, 1, 1))));
            Assert.True(BlacklistForm.Writable(Entry(), Ending(new DateTime(9998, 12, 31))));
            Assert.False(BlacklistForm.Writable(far, Ending(new DateTime(9999, 12, 31))));
            Assert.False(BlacklistForm.Writable(far, Ending(new DateTime(9999, 6, 1))));
            Assert.False(BlacklistForm.Writable(Entry(), far));
            Assert.False(BlacklistForm.Writable(Entry(), Ending(new DateTime(1999, 12, 31))));
        }

        // The audit log's JSON: fixed keys in a fixed order, the end in UTC to the second or the word unreadable, null for
        // an empty field, and for an edit only what changed
        [Fact]
        public void JsonHasFixedKeys()
        {
            Assert.Equal("""{"Name":"Test Player","Reason":"Testing","Expires":"2027-01-02 03:04:05","Notes":"Made up"}""", BlacklistJson.Whole(Entry()));
            Assert.Equal("""{"Name":null,"Reason":null,"Expires":"unreadable","Notes":null}""",
                BlacklistJson.Whole(new BlacklistModel { steam_id = 1, expire_date_unreadable = true }));

            var edited = Entry();
            edited.reason = null;
            edited.expire_date = new DateTime(2030, 6, 7);

            Assert.Equal(("""{"Reason":"Testing","Expires":"2027-01-02 03:04:05"}""", """{"Reason":null,"Expires":"2030-06-07 00:00:00"}"""),
                BlacklistJson.Changed(Entry(), edited));
            Assert.Equal(((string?)null, (string?)null), BlacklistJson.Changed(Entry(), Entry()));

            // Capitals count: the website and the bot compare names through the table's collation, but what's shown changed
            var capitals = Entry();
            capitals.username = "test player";
            Assert.Equal("""{"Name":"Test Player"}""", BlacklistJson.Changed(Entry(), capitals).Before);
        }

        // The form's fields together, with a message for each that failed, and the boxes kept as they were sent
        [Fact]
        public void FieldsAreCheckedTogether()
        {
            Assert.True(BlacklistForm.TryFields(" Name ", "", "2027-01-02", null, null, out var fields, out var none));
            Assert.Equal(new BlacklistFields("Name", null, new DateTime(2027, 1, 2), null) { Boxes = new BlacklistBoxes(" Name ", "", null) }, fields);
            Assert.Empty(none);

            Assert.False(BlacklistForm.TryFields(new string('n', 61), "a\nb", "2027-02-30", new string('o', 121), null, out _, out var problems));
            Assert.Equal(
                new Dictionary<BlacklistField, string>
                {
                    [BlacklistField.Name] = "The name holds at most 60 characters.",
                    [BlacklistField.Reason] = "The reason can't hold a line break or another control character.",
                    [BlacklistField.Expires] = BlacklistWords.BadDate,
                    [BlacklistField.Notes] = "The notes hold at most 120 characters."
                }, problems);
        }
    }
}
