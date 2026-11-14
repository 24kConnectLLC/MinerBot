using System.Text;
using Discord;
using Microsoft.Extensions.Caching.Memory;
using MinerBot_2._0;
using MinerBot_2._0.Services;
using Steam.Models;

namespace MinerBot.Tests
{
    public class MapEmbedTests
    {
        const int DescriptionLimit = 650;

        readonly SteamAPIService service = new(new FakeGetData(_ => { }), new MemoryCache(new MemoryCacheOptions()));

        Task<Embed> FormatWithDescription(string? description)
        {
            var map = FakeGetData.CreateItem(1);
            map.Description = description!;

            return service.FormatMapEmbed(map);
        }

        static Embed EmbedOfLength(int length) => new EmbedBuilder().WithDescription(new string('a', length)).Build();

        static IEnumerable<int> GroupSizes(params int[] lengths) =>
            SteamAPIService.GroupEmbedsByLength(lengths.Select(EmbedOfLength)).Select(group => group.Length);

        [Theory]
        [InlineData(DescriptionLimit + 1)]
        [InlineData(4097)]
        [InlineData(8000)]
        public async Task LongDescriptionIsShortened(int length)
        {
            var embed = await FormatWithDescription(new string('a', length));

            Assert.Equal(new string('a', DescriptionLimit - 3) + "...", embed.Description);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(DescriptionLimit)]
        public async Task ShortDescriptionStaysTheSame(int length)
        {
            var description = new string('a', length);

            var embed = await FormatWithDescription(description);

            Assert.Equal(description, embed.Description);
        }

        [Fact]
        public async Task MapWithNoDescriptionStillWorks()
        {
            var embed = await FormatWithDescription(null);

            Assert.Null(embed.Description);
        }

        // Sent as no description, like a missing one, rather than as empty text
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("[img]https://example.com/a.png[/img]")]
        [InlineData("[h1][/h1]\n[previewyoutube=abc;full][/previewyoutube]")]
        public async Task DescriptionWithNothingLeftIsSentAsNone(string description)
        {
            var embed = await FormatWithDescription(description);

            Assert.Null(embed.Description);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public async Task ShorteningNeverCutsACharacterInHalf(int lettersBefore)
        {
            const string smiley = "\U0001F600";
            var description = new string('a', lettersBefore) + string.Concat(Enumerable.Repeat(smiley, DescriptionLimit));
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

            var embed = await FormatWithDescription(description);

            Assert.InRange(embed.Description.Length, DescriptionLimit - 1, DescriptionLimit);
            Assert.EndsWith(smiley + "...", embed.Description);
            strictUtf8.GetByteCount(embed.Description);
        }

        // Steam's own markup, which Discord would show as typed. A picture or video goes with only its own content, and
        // bracketed text that isn't one of Steam's tags stays.
        [Theory]
        [InlineData("[b]Bold[/b] [u]u[/u] [i]i[/i] [strike]s[/strike]", "Bold u i s")]
        [InlineData("[H1]Big[/H1] [h2]Mid[/h2] [h3]Small[/h3]", "Big Mid Small")]
        [InlineData("[url=https://evil.example/x]Click here[/url]", "Click here")]
        [InlineData("[url]https://example.com/[/url]", "https://example.com/")]
        [InlineData("a[img]https://example.com/a.png[/img]b", "ab")]
        [InlineData("a[IMG]x[/IMG]b[img]y", "aby")]
        [InlineData("a[previewyoutube=dQw4w9WgXcQ;full][/previewyoutube]b", "ab")]
        [InlineData("a[img]x.png[/img]b[img]y.png[/img]c", "abc")]
        [InlineData("a[previewyoutube=x;full][/previewyoutube]b[previewyoutube=y;full][/previewyoutube]c", "abc")]
        [InlineData("[quote=author]Quoted[/quote]", "Quoted")]
        [InlineData("[code]x = 1[/code] [noparse]np[/noparse] [spoiler]sp[/spoiler]", "x = 1 np sp")]
        [InlineData("a[hr][/hr]b", "a\nb")]
        [InlineData("[table noborder=1][tr][th]h[/th][td]d[/td][/tr][/table]", "h d")]
        [InlineData("[list]\n[*]One\n[*] Two\n[/list]", "\u2022 One\n\u2022 Two")]
        [InlineData("Steps:[olist][*]One[*]Two[/olist]", "Steps:\n\u2022 One\n\u2022 Two")]
        [InlineData("[WIP] map [v2]", "[WIP] map [v2]")]
        [InlineData("[bold] [h4]x[/h4] [urlx] [ b]", "[bold] [h4]x[/h4] [urlx] [ b]")]
        [InlineData("  [b] [/b]  ", "")]
        // An attribute always has an equals sign, so a word after a tag's name is bracketed text
        [InlineData("Tip: [spoiler alert] the exit is behind the waterfall.", "Tip: [spoiler alert] the exit is behind the waterfall.")]
        [InlineData("[i think] [b careful] [url in my profile] [list of modes: FFA, TDM] [hr line]", "[i think] [b careful] [url in my profile] [list of modes: FFA, TDM] [hr line]")]
        [InlineData("[table equalcells=1 noborder=1][tr][td]x[/td][/tr][/table] [b ]y[/b ]", "x y")]
        // The same goes for a picture or a video, even with a closing tag after it
        [InlineData("See [img of the arena] here [/img] end", "See [img of the arena] here  end")]
        [InlineData("[previewyoutube of the trailer] x [/previewyoutube] y", "[previewyoutube of the trailer] x  y")]
        // A picture missing its closing tag doesn't take the text up to the next picture's
        [InlineData("[img]a.png Please read. [img]b.png[/img] Have fun!", "a.png Please read.  Have fun!")]
        [InlineData("[previewyoutube=abc;full] Trailer. [previewyoutube=def;full][/previewyoutube] End", "Trailer.  End")]
        [InlineData("See [img of the arena] then [img]a.png[/img] enjoy", "See [img of the arena] then  enjoy")]
        // Steam shows these on their own line or in their own cell, so taking them out mustn't join two words
        [InlineData("[h1]Title[/h1]Text right after", "Title\nText right after")]
        [InlineData("[table][tr][th]Mode[/th][th]Players[/th][/tr][tr][td]FFA[/td][td]8[/td][/tr][/table]", "Mode Players\nFFA 8")]
        [InlineData("x[quote=Bob]q[/quote]y [b]b[/b]c", "x\nq\ny bc")]
        [InlineData("x[h2]y[/h2]z", "x\ny\nz")]
        [InlineData("x[h3]y[/h3]z", "x\ny\nz")]
        [InlineData("x[code]y[/code]z", "x\ny\nz")]
        [InlineData("a[list][*]b[/list]c", "a\n\u2022 b\nc")]
        [InlineData("a[olist][*]b[/olist]c", "a\n\u2022 b\nc")]
        [InlineData("a[table][td]b[/td][/table]c", "a\nb\nc")]
        // Discord drops a form feed, so one beside the tags doesn't count as a space between the words
        [InlineData("[h1]Title[/h1]\fText", "Title\nText")]
        [InlineData("x\f[h1]y[/h1]z", "x\ny\nz")]
        [InlineData("[table][tr][th]Mode[/th]\f[th]Players[/th][/tr][/table]", "Mode Players")]
        public void BBCodeIsStripped(string text, string expected)
        {
            Assert.Equal(expected, WorkshopText.StripBBCode(text));
        }

        // What Discord would read as markdown, a mention or a timestamp shows as typed, with each mark escaped
        [Theory]
        [InlineData("[Click here](https://evil.example/x)", @"\[Click here](https\:\/\/evil\.example\/x)")]
        [InlineData("**bold** __underline__ *italics* ~~strike~~", @"\*\*bold\*\* \_\_underline\_\_ \*italics\* \~\~strike\~\~")]
        [InlineData("# Heading", @"\# Heading")]
        [InlineData("Text\n## Heading", "Text\n\\## Heading")]
        [InlineData("-# subtext", @"\-# subtext")]
        [InlineData("- item\n  - nested\n+ plus\n* star\n1. first", "\\- item\n  \\- nested\n\\+ plus\n\\* star\n1\\. first")]
        [InlineData("> quote\n>>> block", "\\> quote\n\\>\\>\\> block")]
        [InlineData("`code` ```block```", @"\`code\` \`\`\`block\`\`\`")]
        [InlineData("||spoiler||", @"\|\|spoiler\|\|")]
        [InlineData("<@80351110224678912> <@&123> <#456>", @"\<@80351110224678912\> \<@&123\> \<#456\>")]
        [InlineData("<t:1618953630:R>", @"\<t\:1618953630\:R\>")]
        [InlineData("<:mmLol:216154654256398347>", @"\<\:mmLol\:216154654256398347\>")]
        [InlineData("<https://example.com>", @"\<https\:\/\/example\.com\>")]
        [InlineData(@"a\b", @"a\\b")]
        public async Task MarkdownInADescriptionIsShownAsText(string description, string expected)
        {
            var embed = await FormatWithDescription(description);

            Assert.Equal(expected, embed.Description);
        }

        // Discord's parser makes a lone carriage return a new line and drops form feeds before it reads where a line starts,
        // so a mark after either starts a line too
        [Theory]
        [InlineData("Line one\r# Heading", "Line one\n\\# Heading")]
        [InlineData("Intro\r- item\r-# small\r+ plus", "Intro\n\\- item\n\\-# small\n\\+ plus")]
        [InlineData("Line one\n\r# Heading", "Line one\n\n\\# Heading")]
        [InlineData("Line one\n\f# Heading", "Line one\n\\# Heading")]
        [InlineData("Line one\n \f ## Heading", "Line one\n  \\## Heading")]
        [InlineData("Line one\r\n# Heading", "Line one\n\\# Heading")]
        public async Task MarkAfterAnyLineBreakIsEscaped(string description, string expected)
        {
            var embed = await FormatWithDescription(description);

            Assert.Equal(expected, embed.Description);
        }

        // Stripped first, so what's left of the markup is escaped like any other text
        [Fact]
        public async Task BBCodeIsTakenOutBeforeTheEscaping()
        {
            var embed = await FormatWithDescription("[h1]Title[/h1]\n[b]Bold[/b] [url=https://evil.example/x]Click here[/url]\n[img]https://example.com/a.png[/img][list]\n[*]One\n[*]Two\n[/list]\n[WIP] [spoiler]S[/spoiler]");

            Assert.Equal("Title\nBold Click here\n\n\u2022 One\n\u2022 Two\n\n\\[WIP] S", embed.Description);
        }

        // Escaping lengthens the text, so the cut comes after it
        [Fact]
        public async Task DescriptionIsCutAfterItsEscaped()
        {
            var embed = await FormatWithDescription(string.Concat(Enumerable.Repeat("a.", DescriptionLimit / 2)));

            Assert.Equal(string.Concat(Enumerable.Repeat(@"a\.", 215)) + "a...", embed.Description);
        }

        // A backslash cut from its character would escape the first dot of the ending instead
        [Fact]
        public async Task CutNeverLeavesABackslashWithoutItsCharacter()
        {
            var embed = await FormatWithDescription(new string('a', DescriptionLimit - 4) + "*bbbb");

            Assert.Equal(new string('a', DescriptionLimit - 4) + "...", embed.Description);
        }

        [Fact]
        public async Task CutKeepsAnEscapedBackslashWhole()
        {
            var embed = await FormatWithDescription(new string('a', DescriptionLimit - 5) + @"\bbbbbb");

            Assert.Equal(new string('a', DescriptionLimit - 5) + @"\\...", embed.Description);
        }

        [Fact]
        public async Task CutNeverSplitsAnEmojiAfterAnEscape()
        {
            const string smiley = "\U0001F600";
            var embed = await FormatWithDescription(new string('a', DescriptionLimit - 6) + "*" + string.Concat(Enumerable.Repeat(smiley, 5)));

            Assert.Equal(new string('a', DescriptionLimit - 6) + @"\*...", embed.Description);
        }

        static string Field(Embed embed, string name) => Assert.Single(embed.Fields, field => field.Name == name).Value;

        // Discord shows a timestamp in each reader's own zone and language. Steam's times are UTC without saying so, so
        // they mustn't be read in the zone of the machine the bot runs on.
        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Utc)]
        public async Task TimesAreDiscordTimestamps(DateTimeKind kind)
        {
            var map = FakeGetData.CreateItem(1);
            map.TimeCreated = new DateTime(2026, 11, 1, 13, 45, 0, kind);
            map.TimeUpdated = new DateTime(2026, 11, 2, 9, 30, 0, kind);

            var embed = await service.FormatMapEmbed(map);

            Assert.Equal("<t:1793540700:f>", Field(embed, "Time Created:"));
            Assert.Equal("<t:1793611800:f>", Field(embed, "Time Updated:"));
        }

        [Fact]
        public async Task MissingTimeShowsUnknown()
        {
            var map = FakeGetData.CreateItem(1);
            map.TimeCreated = default;
            map.TimeUpdated = default;

            var embed = await service.FormatMapEmbed(map);

            Assert.Equal("Unknown", Field(embed, "Time Created:"));
            Assert.Equal("Unknown", Field(embed, "Time Updated:"));
        }

        // Steam sends an empty list for a map without tags. Its reader leaves Tags null only when a reply has no list at all,
        // as in Steam's placeholder for a removed map.
        [Fact]
        public async Task MapWithoutTagsShowsNone()
        {
            var map = FakeGetData.CreateItem(1);

            map.Tags = [];
            Assert.Equal("None", Field(await service.FormatMapEmbed(map), "Tags:"));

            map.Tags = null!;
            Assert.Equal("None", Field(await service.FormatMapEmbed(map), "Tags:"));

            map.Tags = ["", " "];
            Assert.Equal("None", Field(await service.FormatMapEmbed(map), "Tags:"));
        }

        [Fact]
        public async Task TagsAreEscaped()
        {
            var map = FakeGetData.CreateItem(1);
            map.Tags = ["Team_Deathmatch", "<@123>", "# Big", "Map"];

            Assert.Equal(@"Team\_Deathmatch, \<@123\>, \# Big, Map", Field(await service.FormatMapEmbed(map), "Tags:"));
        }

        [Fact]
        public async Task TagWithALoneCarriageReturnIsEscaped()
        {
            var map = FakeGetData.CreateItem(1);
            map.Tags = ["a\r# b"];

            Assert.Equal("a\n\\# b", Field(await service.FormatMapEmbed(map), "Tags:"));
        }

        // A field holds 1,024 characters, and one over fails the whole post
        [Fact]
        public async Task EscapedTagsAreCutToWhatAFieldHolds()
        {
            var map = FakeGetData.CreateItem(1);
            map.Tags = Enumerable.Range(1, 100).Select(number => $"Tag_{number}.").ToList();

            var tags = Field(await service.FormatMapEmbed(map), "Tags:");

            Assert.InRange(tags.Length, EmbedFieldBuilder.MaxFieldValueLength - 1, EmbedFieldBuilder.MaxFieldValueLength);
            Assert.EndsWith("...", tags);
        }

        [Fact]
        public void EmbedsThatFitStayInOneGroup()
        {
            Assert.Equal([3], GroupSizes(2000, 2000, 2000));
        }

        [Fact]
        public void EmbedThatPassesTheLimitStartsANewGroup()
        {
            Assert.Equal([2, 1], GroupSizes(2000, 2000, 2001));
        }

        [Fact]
        public void EveryGroupStaysWithinTheLimit()
        {
            Assert.Equal([2, 2, 1], GroupSizes(4000, 1500, 1500, 3000, 3000));
        }

        [Fact]
        public void GroupsKeepTheOrderOfTheEmbeds()
        {
            var embeds = new[] { 4000, 3000, 2000 }.Select(EmbedOfLength).ToList();

            Assert.Equal(embeds, SteamAPIService.GroupEmbedsByLength(embeds).SelectMany(group => group));
        }
    }

    [Collection("Discord")]
    public class MapEmbedFlowTests
    {
        const string MapUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890";

        // The description is at its longest after shortening, and the tags take up most of what a field can hold
        internal static PublishedFileDetailsModel LongMap(int id)
        {
            var map = FakeGetData.CreateItem(id);
            map.Description = new string('a', 5000);
            map.Tags = Enumerable.Range(1, 120).Select(number => $"Tag {number}").ToList();

            return map;
        }

        static string ShortDescription(Step step) => Assert.Single(step.EmbedDescriptions);

        [Fact]
        public async Task MapLookupShowsAMapWithALongDescription()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.WorkshopItem = LongMap(1);

            await discord.SendSlashCommandAsync("maps", "lookup", $$"""{"name":"map-url","type":3,"value":"{{MapUrl}}"}""");

            Assert.EndsWith("...", ShortDescription(discord.Single("followup")));
        }

        [Fact]
        public async Task LatestMapsShowsAMapWithALongDescription()
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Steam.LatestItems[0] = LongMap(1);

            await discord.SendSlashCommandAsync("maps", "latest");

            Assert.EndsWith("...", ShortDescription(discord.Single("followup")));
        }
    }
}
