using MinerBot.Dashboard.Requests;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Tests
{
    // The typed words of the controls that are hard to take back, compared on the server: each control's word, without the
    // white space around it and in any case, and a server's name as it reads, or its ID, which always works
    public class ConfirmationTests
    {
        const ulong ServerId = 1000000000000000701;
        const string ServerIdText = "1000000000000000701";

        // Fails if a word other than the control's own, a part of it, or an empty box confirms it
        [Theory]
        [InlineData(RequestKinds.BotRestart, "restart")]
        [InlineData(RequestKinds.PostsPause, "pause")]
        [InlineData(RequestKinds.SteamReconnect, "reconnect")]
        [InlineData(RequestKinds.SteamRenew, "renew")]
        public void EachWordIsCheckedTrimmedAndIgnoringCase(string kind, string word)
        {
            Assert.Equal(word, Confirmations.WordFor(RequestKinds.Find(kind)!));

            foreach (var typed in new[] { word, " " + word + "  ", word.ToUpperInvariant(), char.ToUpperInvariant(word[0]) + word[1..] + "\t" })
                Assert.True(Confirmations.MatchesWord(typed, word), $"'{typed}' didn't confirm {word}");

            foreach (var typed in new[] { null, "", " ", word[..^1], word + "s", word + " now", "yes", "confirm" })
                Assert.False(Confirmations.MatchesWord(typed, word), $"'{typed}' confirmed {word}");
        }

        // The plan's restart, pause and Steam controls each take their word, a leave takes the server, and nothing else asks
        // for anything typed
        [Fact]
        public void OnlyTheControlsHardToTakeBackNeedTyping()
        {
            Assert.Equal([RequestKinds.BotRestart, RequestKinds.PostsPause, RequestKinds.SteamReconnect, RequestKinds.SteamRenew],
                RequestKinds.All.Where(kind => Confirmations.WordFor(kind) != null).Select(kind => kind.Word).Order(StringComparer.Ordinal));
            Assert.Equal([RequestKinds.GuildLeave], RequestKinds.All.Where(Confirmations.NeedsServer).Select(kind => kind.Word));
        }

        // Discord names can hold characters that show nothing or turn the text around, which the page shows as codes and
        // nobody can type: the name matches what it shows, in any case, with its white space evened out and letters drawn in
        // another style read as the plain ones. Fails if the name is compared as it's stored.
        [Fact]
        public void AServerNameIsComparedWithoutHiddenCharacters()
        {
            var name = "Test\u200B Server\u202E";

            Assert.True(Confirmations.MatchesServer("Test Server", name, ServerId));
            Assert.True(Confirmations.MatchesServer("test server", name, ServerId));
            Assert.True(Confirmations.MatchesServer("  TEST   SERVER ", name, ServerId));
            Assert.True(Confirmations.MatchesServer("\uFF34\uFF45\uFF53\uFF54 Server", name, ServerId));
            Assert.True(Confirmations.MatchesServer(name, name, ServerId));

            Assert.False(Confirmations.MatchesServer("TestServer", name, ServerId));
            Assert.False(Confirmations.MatchesServer("Another", name, ServerId));
            Assert.False(Confirmations.MatchesServer("Test Server 2", name, ServerId));
            Assert.False(Confirmations.MatchesServer(null, name, ServerId));
            Assert.False(Confirmations.MatchesServer("", name, ServerId));

            Assert.Equal("Test Server", Confirmations.Simple(name));
        }

        // The page shows each of those characters as its code, such as [U+200B], and asks for the name as it shows, so the
        // name with its codes, as copying it from the page gives it, confirms too, in any case and with its white space
        // evened out; a Hangul filler, which Discord names often hold, too. Another code, or the name run together, doesn't.
        // Fails if the name is only compared with its hidden characters left out.
        [Fact]
        public void TheNameAsThePageShowsItConfirms()
        {
            var name = "Test\u200B Server\u202E";
            var shown = TextInspector.Reveal(name);

            Assert.Equal("Test[U+200B] Server[U+202E]", shown);
            Assert.True(Confirmations.MatchesServer(shown, name, ServerId));
            Assert.True(Confirmations.MatchesServer(" test[u+200b]   SERVER[u+202e] ", name, ServerId));
            Assert.True(Confirmations.MatchesServer("[U+3164]Murder Miners[U+3164]", "\u3164Murder Miners\u3164", ServerId));
            Assert.True(Confirmations.MatchesServer("Murder Miners", "\u3164Murder Miners\u3164", ServerId));

            Assert.False(Confirmations.MatchesServer("Test[U+200C] Server[U+202E]", name, ServerId));
            Assert.False(Confirmations.MatchesServer("Test[U+200B]Server[U+202E]", name, ServerId));
            Assert.False(Confirmations.MatchesServer("Test[U+200B] Server[U+202E] 2", name, ServerId));
        }

        // The ID in digits always confirms a leave, whatever the name holds, and nothing like it does
        [Fact]
        public void AServersIdIsAlwaysAccepted()
        {
            Assert.True(Confirmations.MatchesServer(ServerIdText, "Test Server", ServerId));
            Assert.True(Confirmations.MatchesServer(" " + ServerIdText + " ", "Test Server", ServerId));
            Assert.True(Confirmations.MatchesServer(ServerIdText, null, ServerId));
            Assert.True(Confirmations.MatchesServer(ServerIdText, "\u200B\u202E", ServerId));

            Assert.False(Confirmations.MatchesServer("1000000000000000702", "Test Server", ServerId));
            Assert.False(Confirmations.MatchesServer("0" + ServerIdText, "Test Server", ServerId));
            Assert.False(Confirmations.MatchesServer(ServerIdText + "x", "Test Server", ServerId));
        }

        // A name with nothing anyone can type, such as one made only of characters that show nothing, takes the ID alone: an
        // empty box, one of spaces, one of the same hidden characters, or their codes as the page shows them never matches it
        [Fact]
        public void ANameWithNothingToTypeTakesOnlyTheId()
        {
            foreach (var name in new[] { "\u200B\u202E", "   ", "", null })
            {
                Assert.False(Confirmations.MatchesServer("", name, ServerId));
                Assert.False(Confirmations.MatchesServer(" ", name, ServerId));
                Assert.False(Confirmations.MatchesServer("\u200B\u202E", name, ServerId));
                Assert.False(Confirmations.MatchesServer("[U+200B][U+202E]", name, ServerId));
                Assert.False(Confirmations.MatchesServer(null, name, ServerId));
                Assert.True(Confirmations.MatchesServer(ServerIdText, name, ServerId));
            }
        }

        // Text .NET's normalization refuses, such as half of a surrogate pair, in the box or in the name, is no match, never
        // an exception the page would turn into an error page
        [Fact]
        public void BrokenUnicodeIsNoMatchNotAnError()
        {
            const string lone = "\uD800";

            Assert.Null(Confirmations.Simple("Test " + lone));
            Assert.False(Confirmations.MatchesServer("Test " + lone, "Test " + lone, ServerId));
            Assert.False(Confirmations.MatchesServer("Test Server", "Test Server" + lone, ServerId));
            Assert.False(Confirmations.MatchesServer("Test Server" + lone, "Test Server", ServerId));
            Assert.True(Confirmations.MatchesServer(ServerIdText, "Test " + lone, ServerId));
        }

        // A hidden character outside the Basic Multilingual Plane is two of .NET's characters, and both go: a language tag,
        // and the tags that make a flag such as Scotland's out of the black flag, which shows. Fails if half of one is left
        // behind, so such a name never matches what it shows and takes only the ID.
        [Fact]
        public void AHiddenCharacterOutsideTheBasicPlaneGoesWhole()
        {
            var tagged = "Test\U000E0001 Server";
            var flag = "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F Scotland";

            Assert.Equal("Test Server", Confirmations.Simple(tagged));
            Assert.True(Confirmations.MatchesServer("Test Server", tagged, ServerId));
            Assert.True(Confirmations.MatchesServer("test server", tagged, ServerId));
            Assert.True(Confirmations.MatchesServer("\U0001F3F4 Scotland", flag, ServerId));
            Assert.False(Confirmations.MatchesServer("Scotland", flag, ServerId));
        }
    }
}
