using MinerBot.Shared.Requests;

namespace MinerBot.Shared.Tests
{
    // What a request must be before the dashboard signs it and once the bot has verified it. The awkward texts are made in
    // the test rather than given as theory data, which shows its values in the test's name.
    public class RequestRulesTests
    {
        static readonly DateTime Made = new(2026, 11, 20, 15, 4, 5, 123, DateTimeKind.Utc);

        const ulong OwnerId = 123456789012345678, GuildId = 345678901234567890, ChannelId = 456789012345678901;

        // A request of the kind with what its table says it takes
        static RequestFields Good(string word)
        {
            var kind = RequestKinds.Find(word)!;
            var argument = kind.Argument switch
            {
                RequestArgument.Reason => "Live check",
                RequestArgument.StatusText => "Back soon \U0001F3AE",
                RequestArgument.CustomLinkName => "some_name",
                _ => null
            };

            return RequestFields.New("00112233445566778899aabbccddeeff", kind, OwnerId, "Test Owner", RequestRoles.Owner, kind.NeedsServer ? GuildId : null,
                kind.NeedsChannel ? ChannelId : null, argument, Made);
        }

        static RequestFields Pause => Good(RequestKinds.PostsPause);

        [Theory]
        [InlineData("the default", true)]
        [InlineData("empty", false)]
        [InlineData("one character", true)]
        [InlineData("128 characters", true)]
        [InlineData("129 characters", false)]
        [InlineData("an emoji at the limit", true)]
        [InlineData("an emoji past the limit", false)]
        [InlineData("emoji joined into one", true)]
        [InlineData("the flags of England, Scotland and Wales", true)]
        [InlineData("a black flag with other tags", false)]
        [InlineData("tags after the flag of England", false)]
        [InlineData("the flag of England without its cancel tag", false)]
        [InlineData("the tags of England's flag without the black flag", false)]
        [InlineData("the tags of Scotland's flag after the flag of England", false)]
        [InlineData("a combining mark", true)]
        [InlineData("a line break", false)]
        [InlineData("a carriage return", false)]
        [InlineData("a line separator", false)]
        [InlineData("a tab", false)]
        [InlineData("a zero-width space", false)]
        [InlineData("a right-to-left override", false)]
        [InlineData("@everyone", false)]
        [InlineData("a user mention", false)]
        [InlineData("a link", false)]
        [InlineData("a link without its scheme", false)]
        [InlineData("an invite", false)]
        [InlineData("a masked link", false)]
        [InlineData("a masked link to an address without a scheme", false)]
        [InlineData("a masked link to an invite", false)]
        [InlineData("a masked link whose label is a link", false)]
        [InlineData("clan tags before regions", true)]
        [InlineData("an address without a scheme", false)]
        [InlineData("an address in a sentence", false)]
        [InlineData("a short link", false)]
        [InlineData("an address alone", false)]
        [InlineData("an address in fullwidth letters", false)]
        [InlineData("an address with a lookalike letter", false)]
        [InlineData("an address with a fullwidth full stop", false)]
        [InlineData("an address with an ideographic full stop", false)]
        [InlineData("an address with a small full stop", false)]
        [InlineData("an address with a one dot leader", false)]
        [InlineData("an IP address with a path", false)]
        [InlineData("an IP address with a port", false)]
        [InlineData("an IP address alone", false)]
        [InlineData("an IP address in fullwidth digits", false)]
        [InlineData("an address with a fullwidth slash", false)]
        [InlineData("a scheme with a fullwidth colon", false)]
        [InlineData("an address with a style selector after the dot", false)]
        [InlineData("an address with a text style selector after the dot", false)]
        [InlineData("an address with an ideographic full stop after a lookalike letter", false)]
        [InlineData("an IP address with ideographic full stops", false)]
        [InlineData("an IP address with a style selector after a dot", false)]
        [InlineData("an IP address with a style selector after a number", false)]
        [InlineData("an IP address with a style selector after its last digit", false)]
        [InlineData("an IP address with a style selector inside a number", false)]
        [InlineData("an IP address after an emoji", false)]
        [InlineData("an IP address after a keycap", false)]
        [InlineData("an IP address after the information emoji", false)]
        [InlineData("an IP address before a keycap", false)]
        [InlineData("an IP address before the information emoji", false)]
        [InlineData("an address before a keycap", false)]
        [InlineData("an address in mathematical bold letters", false)]
        [InlineData("an IP address in mathematical bold digits", false)]
        [InlineData("an address in circled letters", false)]
        [InlineData("words in mathematical bold letters", true)]
        [InlineData("an ellipsis between two sentences", true)]
        [InlineData("a version number", true)]
        [InlineData("a version number of four parts after a v", true)]
        [InlineData("an abbreviation", true)]
        [InlineData("two sentences", true)]
        [InlineData("two sentences in Japanese", true)]
        [InlineData("two sentences in Japanese with a fullwidth full stop", true)]
        [InlineData("two sentences in Chinese", true)]
        [InlineData("a time with a dot", true)]
        [InlineData("a leading space", false)]
        [InlineData("a trailing space", false)]
        [InlineData("a lone surrogate", false)]
        [InlineData("U+FFFE, which .NET's normalization refuses", false)]
        public void StatusTextRules(string example, bool allowed)
        {
            string? text = example switch
            {
                "the default" => null,
                "empty" => "",
                "one character" => "a",
                "128 characters" => new string('a', 128),
                "129 characters" => new string('a', 129),
                // An emoji outside the Basic Multilingual Plane counts two, as Discord counts it
                "an emoji at the limit" => new string('a', 126) + "\U0001F3AE",
                "an emoji past the limit" => new string('a', 127) + "\U0001F3AE",
                "emoji joined into one" => "Back soon \U0001F3F4\u200D\u2620\uFE0F",
                // A black flag, tag letters naming the region and a cancel tag show as one flag, as Discord's emoji picker
                // gives them. Only those three sequences: any other tag can hide text.
                "the flags of England, Scotland and Wales" => "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F vs "
                    + "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F, \U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F next",
                "a black flag with other tags" => "\U0001F3F4\U000E0061\U000E0062\U000E0063\U000E007F Back soon",
                "tags after the flag of England" => "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F\U000E0061\U000E0062\U000E0063 Back soon",
                "the flag of England without its cancel tag" => "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067 Back soon",
                "the tags of England's flag without the black flag" => "Back soon \U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F",
                "the tags of Scotland's flag after the flag of England" => "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F"
                    + "\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F Back soon",
                "a combining mark" => "Cafe\u0301 open",
                "a line break" => "Back\nsoon",
                "a carriage return" => "Back\rsoon",
                "a line separator" => "Back\u2028soon",
                "a tab" => "Back\tsoon",
                "a zero-width space" => "Back\u200Bsoon",
                "a right-to-left override" => "Back \u202Esoon",
                "@everyone" => "Back soon @everyone",
                "a user mention" => "Ask <@1>",
                "a link" => "https://x",
                "a link without its scheme" => "www.x",
                "an invite" => "discord.gg/x",
                "a masked link" => "[Back soon](https://x)",
                "a masked link to an address without a scheme" => "[Free Nitro](evil.example/a)",
                "a masked link to an invite" => "[Join](discord.gg/abc)",
                // No dot in the label's link, as in the first masked link's target, so the address pattern can't see it
                "a masked link whose label is a link" => "[https://x](EU)",
                // Discord's masked-link form shows as it's written in a status text, and with no address in it, it's no link
                "clan tags before regions" => "[MM](EU) vs [XX](NA) in the finals",
                // A status text is plain text everywhere it shows, so an address someone can type out counts too
                "an address without a scheme" => "x.com/minerbot",
                "an address in a sentence" => "Free Nitro at discord-gift.com/claim",
                "a short link" => "dsc.gg/minerbot",
                "an address alone" => "Visit minerbot.gg!",
                "an address in fullwidth letters" => "\uFF44\uFF49\uFF53\uFF43\uFF4F\uFF52\uFF44.gg/abc",
                "an address with a lookalike letter" => "\u0501iscord.gg/abc",
                "an address with a fullwidth full stop" => "discord\uFF0Egg/abc",
                "an address with an ideographic full stop" => "discord\u3002gg/abc",
                "an address with a small full stop" => "discord\uFE52gg/abc",
                "an address with a one dot leader" => "x\u2024com/minerbot",
                "an IP address with a path" => "Free Nitro at 203.0.113.5/nitro",
                "an IP address with a port" => "Join 198.51.100.7:27015",
                "an IP address alone" => "Visit 203.0.113.5",
                "an IP address in fullwidth digits" => "Visit \uFF12\uFF10\uFF13.0.113.5/nitro",
                "an address with a fullwidth slash" => "discord.gg\uFF0Fabc",
                "a scheme with a fullwidth colon" => "HTTPS\uFF1A//x.example",
                // A style selector draws nothing after a dot, and a browser ignores it in an address
                "an address with a style selector after the dot" => "discord.\uFE0Fgg/abc",
                "an address with a text style selector after the dot" => "x.\uFE0Ecom/minerbot",
                "an address with an ideographic full stop after a lookalike letter" => "discor\u0501\u3002gg/abc",
                "an IP address with ideographic full stops" => "Visit 203\u30020\u3002113\u30025/nitro",
                "an IP address with a style selector after a dot" => "Visit 203.\uFE0F0.113.5/nitro",
                // After a digit a style selector styles it, as in a keycap, so it isn't hidden, and a browser ignores
                // it in an address too. A phone types an arrow emoji with a style selector after it.
                "an IP address with a style selector after a number" => "Visit 203\uFE0F.0.113.5/nitro",
                "an IP address with a style selector after its last digit" => "Visit 203.0.113.5\uFE0F/nitro",
                "an IP address with a style selector inside a number" => "Visit 2\uFE0F03.0.113.5/nitro",
                "an IP address after an emoji" => "Free Nitro \u27A1\uFE0F203.0.113.5/nitro",
                // A keycap is a digit that a mark closes in, and Unicode counts the information sign as a letter,
                // but both show as emoji, so an address next to one still counts. Before a keycap, the last number
                // has three digits, so the keycap's digit can't join it.
                "an IP address after a keycap" => "Lucky 7\uFE0F\u20E3203.0.113.5/nitro",
                "an IP address after the information emoji" => "Free Nitro \u2139\uFE0F203.0.113.5/nitro",
                "an IP address before a keycap" => "Free Nitro 203.0.113.255" + "7\uFE0F\u20E3",
                "an IP address before the information emoji" => "Free Nitro 203.0.113.5\u2139\uFE0F/nitro",
                "an address before a keycap" => "Free Nitro nitro-gift.com" + "7\uFE0F\u20E3",
                // What a "fancy text" maker writes, which a browser takes to plain letters and digits in an address
                "an address in mathematical bold letters" => "\U0001D41D\U0001D422\U0001D42C\U0001D41C\U0001D428\U0001D42B\U0001D41D.\U0001D420\U0001D420/abc",
                "an IP address in mathematical bold digits" => "Visit \U0001D7D0\U0001D7CE\U0001D7D1.0.113.5",
                "an address in circled letters" => "\u24D3\u24D8\u24E2\u24D2\u24DE\u24E1\u24D3.\u24D6\u24D6/abc",
                "words in mathematical bold letters" => "\U0001D401\U0001D41A\U0001D41C\U0001D424 soon",
                "an ellipsis between two sentences" => "Back soon\u2026Maps tonight",
                "a version number" => "Murder Miners v2.0.1",
                "a version number of four parts after a v" => "Murder Miners v2.0.1.4",
                "an abbreviation" => "New maps, e.g. Factory",
                "two sentences" => "Back soon. Maps tonight",
                // Chinese and Japanese put no space after a sentence's full stop
                "two sentences in Japanese" => "\u30E1\u30F3\u30C6\u30CA\u30F3\u30B9\u4E2D\u3002\u307E\u3082\u306A\u304F\u623B\u308A\u307E\u3059",
                "two sentences in Japanese with a fullwidth full stop" => "\u30E1\u30F3\u30C6\u30CA\u30F3\u30B9\u4E2D\uFF0E\u307E\u3082\u306A\u304F\u623B\u308A\u307E\u3059",
                "two sentences in Chinese" => "\u7EF4\u62A4\u4E2D\u3002\u7A0D\u540E\u56DE\u6765",
                "a time with a dot" => "Back at 18.30 UTC",
                "a leading space" => " Back soon",
                "a trailing space" => "Back soon ",
                "a lone surrogate" => "Back soon\uD800",
                "U+FFFE, which .NET's normalization refuses" => "Back soon\uFFFE",
                _ => throw new ArgumentOutOfRangeException(nameof(example), example, "The test has no such example")
            };

            var problem = RequestRules.StatusTextProblem(text);

            Assert.True(allowed == (problem == null), $"{example}: {problem ?? "allowed"}");
            Assert.True(allowed == (RequestRules.DetailsProblem(Good(RequestKinds.StatusSet) with { Argument = text }) == null));

            // The problem names the rule, never the text
            if (problem != null && text is { Length: > 3 })
                Assert.DoesNotContain(text, problem);
        }

        [Theory]
        [InlineData("missing", false)]
        [InlineData("empty", false)]
        [InlineData("one character", true)]
        [InlineData("200 characters", true)]
        [InlineData("200 characters with an emoji", true)]
        [InlineData("201 characters", false)]
        [InlineData("a line break", false)]
        [InlineData("a carriage return", false)]
        [InlineData("a paragraph separator", false)]
        [InlineData("a tab", false)]
        [InlineData("a zero-width space", false)]
        [InlineData("a right-to-left override", false)]
        [InlineData("a leading space", false)]
        [InlineData("a trailing space", false)]
        [InlineData("a lone surrogate", false)]
        [InlineData("a link", true)]
        [InlineData("a mention", true)]
        [InlineData("the flag of Wales", true)]
        [InlineData("a black flag with other tags", false)]
        [InlineData("the tags of Scotland's flag after the flag of England", false)]
        public void PauseReasonRules(string example, bool allowed)
        {
            string? reason = example switch
            {
                "missing" => null,
                "empty" => "",
                "one character" => "a",
                "200 characters" => new string('r', 200),
                // MariaDB counts code points, so an emoji is one
                "200 characters with an emoji" => new string('r', 199) + "\U0001F600",
                "201 characters" => new string('r', 201),
                "a line break" => "Live\ncheck",
                "a carriage return" => "Live\rcheck",
                "a paragraph separator" => "Live\u2029check",
                "a tab" => "Live\tcheck",
                "a zero-width space" => "Live\u200Bcheck",
                "a right-to-left override" => "Live \u202Echeck",
                "a leading space" => " Live check",
                "a trailing space" => "Live check ",
                "a lone surrogate" => "Live check\uDC00",
                // Only the owner's direct message shows a reason, inside a code span
                "a link" => "Moving servers, see https://murderminershub.com",
                "a mention" => "Quiet for @everyone",
                "the flag of Wales" => "Moving the \U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F lobbies",
                "a black flag with other tags" => "Live check \U0001F3F4\U000E0061\U000E0062\U000E0063\U000E007F",
                "the tags of Scotland's flag after the flag of England" => "Moving the \U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F"
                    + "\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F lobbies",
                _ => throw new ArgumentOutOfRangeException(nameof(example), example, "The test has no such example")
            };

            var problem = RequestRules.ReasonProblem(reason);

            Assert.True(allowed == (problem == null), $"{example}: {problem ?? "allowed"}");
            Assert.True(allowed == (RequestRules.DetailsProblem(Pause with { Argument = reason }) == null));

            if (problem != null && reason is { Length: > 3 })
                Assert.DoesNotContain(reason, problem);
        }

        // ASCII letters and digits, _ and -, checked one character at a time: the shared link pattern ignores case, and so
        // takes the Kelvin sign for a k
        [Theory]
        [InlineData("letters", true)]
        [InlineData("every kind of character", true)]
        [InlineData("32 characters", true)]
        [InlineData("33 characters", false)]
        [InlineData("empty", false)]
        [InlineData("missing", false)]
        [InlineData("a Kelvin sign", false)]
        [InlineData("a long s", false)]
        [InlineData("a space", false)]
        [InlineData("a dot", false)]
        [InlineData("a slash", false)]
        [InlineData("a letter outside ASCII", false)]
        [InlineData("a fullwidth letter", false)]
        public void CustomLinkNameRules(string example, bool allowed)
        {
            string? name = example switch
            {
                "letters" => "abc",
                "every kind of character" => "AZaz09_-",
                "32 characters" => new string('n', 32),
                "33 characters" => new string('n', 33),
                "empty" => "",
                "missing" => null,
                "a Kelvin sign" => "\u212Aelvin",
                "a long s" => "\u017Ftar",
                "a space" => "bad name",
                "a dot" => "bad.name",
                "a slash" => "name/games",
                "a letter outside ASCII" => "caf\u00E9",
                "a fullwidth letter" => "\uFF41bc",
                _ => throw new ArgumentOutOfRangeException(nameof(example), example, "The test has no such example")
            };

            var problem = RequestRules.CustomLinkNameProblem(name);

            Assert.True(allowed == (problem == null), $"{example}: {problem ?? "allowed"}");
            Assert.True(allowed == (RequestRules.DetailsProblem(Good(RequestKinds.SteamLookup) with { Argument = name }) == null));
        }

        // Each part of a row the dashboard would never write, so the bot refuses it before it looks any further
        [Theory]
        [InlineData("a key in capitals")]
        [InlineData("a short key")]
        [InlineData("a key that isn't hexadecimal")]
        [InlineData("no key")]
        [InlineData("a kind in capitals")]
        [InlineData("a kind without a dot")]
        [InlineData("a kind with two dots")]
        [InlineData("a kind over 40 characters")]
        [InlineData("a role in capitals")]
        [InlineData("a role with a dot")]
        [InlineData("a role over 20 characters")]
        [InlineData("no one who asked")]
        [InlineData("an empty name")]
        [InlineData("a name over 100 characters")]
        [InlineData("a name with a line break")]
        [InlineData("a name with a lone surrogate")]
        [InlineData("a text over 200 characters")]
        [InlineData("a text with a lone surrogate")]
        [InlineData("a local time")]
        [InlineData("a time past the millisecond")]
        [InlineData("an expiry at the time it was made")]
        [InlineData("an expiry before it was made")]
        public void RowsNotInTheDashboardsFormAreRefused(string problem)
        {
            var fields = problem switch
            {
                "a key in capitals" => Pause with { RequestKey = "00112233445566778899AABBCCDDEEFF" },
                "a short key" => Pause with { RequestKey = "00112233445566778899aabbccddeef" },
                "a key that isn't hexadecimal" => Pause with { RequestKey = "00112233445566778899aabbccddeefg" },
                "no key" => Pause with { RequestKey = null! },
                "a kind in capitals" => Pause with { Kind = "Posts.Pause" },
                "a kind without a dot" => Pause with { Kind = "pause" },
                "a kind with two dots" => Pause with { Kind = "posts.pause.now" },
                "a kind over 40 characters" => Pause with { Kind = "posts." + new string('p', 35) },
                "a role in capitals" => Pause with { ActorRole = "Owner" },
                "a role with a dot" => Pause with { ActorRole = "server.admin" },
                "a role over 20 characters" => Pause with { ActorRole = new string('o', 21) },
                "no one who asked" => Pause with { ActorId = 0 },
                "an empty name" => Pause with { ActorName = "" },
                "a name over 100 characters" => Pause with { ActorName = new string('n', 101) },
                "a name with a line break" => Pause with { ActorName = "Test\nOwner" },
                "a name with a lone surrogate" => Pause with { ActorName = "Test Owner\uD800" },
                "a text over 200 characters" => Pause with { Argument = new string('t', 201) },
                "a text with a lone surrogate" => Pause with { Argument = "\uDC00" },
                "a local time" => Pause with { CreatedAt = DateTime.SpecifyKind(Made, DateTimeKind.Local) },
                "a time past the millisecond" => Pause with { ExpiresAt = Pause.ExpiresAt.AddTicks(1) },
                "an expiry at the time it was made" => Pause with { ExpiresAt = Made },
                "an expiry before it was made" => Pause with { ExpiresAt = Made.AddMilliseconds(-1) },
                _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "The test has no such case")
            };

            Assert.NotNull(RequestRules.FormProblem(fields));
            Assert.NotNull(RequestRules.Problem(fields));
        }

        // Each limit is the column's, so a row just inside them passes, and a name may have what the dashboard keeps from
        // Discord: an emoji, a zero-width space and a space at the end
        [Fact]
        public void RowsAtTheLimitsPass()
        {
            var fields = Pause with
            {
                ActorName = new string('n', 99) + "\U0001F3AE",
                Argument = new string('r', 199) + "\U0001F600"
            };

            Assert.Null(RequestRules.FormProblem(fields));
            Assert.Null(RequestRules.Problem(fields));
            Assert.Null(RequestRules.FormProblem(Pause with { ActorName = "Zo\u00EB \u200B " }));
            Assert.Null(RequestRules.FormProblem(Pause with { Kind = new string('k', 20) + "." + new string('k', 19), ActorRole = new string('r', 20) }));
        }

        // Each kind with what its table says it takes passes, and the same kind with a server, a channel or a text it doesn't
        // take, or without one it needs, doesn't
        [Fact]
        public void DetailsFitEachKindsTable()
        {
            Assert.All(RequestKinds.All, kind =>
            {
                var good = Good(kind.Word);

                Assert.Null(RequestRules.DetailsProblem(good));
                Assert.Null(RequestRules.Problem(good));

                Assert.Equal(kind.NeedsServer ? "it needs a server" : "it takes no server",
                    RequestRules.DetailsProblem(good with { GuildId = kind.NeedsServer ? null : GuildId }));
                Assert.Equal(kind.NeedsChannel ? "it needs a channel" : "it takes no channel",
                    RequestRules.DetailsProblem(good with { ChannelId = kind.NeedsChannel ? null : ChannelId }));

                if (kind.NeedsServer)
                    Assert.Equal("it needs a server", RequestRules.DetailsProblem(good with { GuildId = 0 }));

                if (kind.NeedsChannel)
                    Assert.Equal("it needs a channel", RequestRules.DetailsProblem(good with { ChannelId = 0 }));

                if (kind.Argument == RequestArgument.None)
                    Assert.Equal("it takes no text", RequestRules.DetailsProblem(good with { Argument = "x" }));
            });

            // The status text's null is the default, and the other texts are needed
            Assert.Null(RequestRules.DetailsProblem(Good(RequestKinds.StatusSet) with { Argument = null }));
            Assert.Equal("the reason is empty", RequestRules.DetailsProblem(Pause with { Argument = null }));
            Assert.Equal("the custom link's name is empty", RequestRules.DetailsProblem(Good(RequestKinds.SteamLookup) with { Argument = null }));
            Assert.Equal("its kind isn't on the request list", RequestRules.DetailsProblem(Pause with { Kind = "posts.stop" }));
        }

        // Before it signs, the dashboard also checks that the kind is on the list, that the role may ask for it, and that the
        // request lasts no longer than its kind allows
        [Fact]
        public void ProblemChecksTheKindTheRoleAndTheExpiry()
        {
            var restart = Good(RequestKinds.BotRestart);

            Assert.Null(RequestRules.Problem(restart));
            Assert.Equal("its kind isn't on the request list", RequestRules.Problem(restart with { Kind = "bot.stop" }));
            Assert.Equal("only the Owner can ask for it", RequestRules.Problem(restart with { ActorRole = RequestRoles.Admin }));
            Assert.Equal("only the Owner or an Admin can ask for it", RequestRules.Problem(Good(RequestKinds.CachesClear) with { ActorRole = RequestRoles.ServerAdmin }));
            Assert.Equal("only the Owner can ask for it", RequestRules.Problem(restart with { ActorRole = "visitor" }));
            Assert.Null(RequestRules.Problem(Good(RequestKinds.GuildRefresh) with { ActorRole = RequestRoles.ServerAdmin }));
            Assert.Equal("it lasts longer than its kind's 2 minutes", RequestRules.Problem(restart with { ExpiresAt = restart.ExpiresAt.AddMilliseconds(1) }));
            Assert.Null(RequestRules.Problem(restart with { ExpiresAt = restart.CreatedAt.AddSeconds(1) }));

            // The form comes first, so a forged row is refused for its form, whatever else is wrong with it
            Assert.Equal(RequestRules.FormProblem(restart with { RequestKey = "x" }), RequestRules.Problem(restart with { RequestKey = "x", Kind = "bot.stop" }));
        }
    }
}
