using System.Globalization;
using DataAccessLibrary.Armor.Models;
using Microsoft.AspNetCore.Html;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    public sealed record EmbedField(string Name, string Value, bool Inline);

    // A Discord embed as the bot builds it, with Discord's markdown left in, so the page can draw it the way Discord would.
    // The image is the screenshot, which the bot attaches only when it fetched it.
    public sealed record EmbedPreview(string? Title, string? TitleLink, string Description, IReadOnlyList<EmbedField> Fields, string? Image);

    // Copies of what the bot sends, so a reviewer sees an armor as servers will. They come from MinerBot 2.0's
    // Services/ArmorService.cs (NewArmorListEmbeds, NewArmorListEmbed, FormatArmorNames, ArmorEmbed and FormatArmorStyle),
    // Utils/UIUtils.cs (shortenTextForEmbed) and Builders/EmbedBuilderTemplate.cs. No test project references both apps,
    // so nothing can compare the copies: a change to those messages has to be made here too, and in ArmorPreviewTests,
    // whose strings are written out the same as MinerBot.Tests' ArmorEmbedTests writes out the bot's.
    internal static class BotMessages
    {
        public const string UploadAddress = "https://murderminershub.com/upload-armor-style/";

        // EmbedBuilderTemplate's, on every armor message: Discord.Color.DarkBlue, which site.css draws as the embed's bar,
        // and the footer's text. Its icon is on 24kconnect.com, which the page can't load, so a plain mark stands in.
        public const string Color = "#206694";
        public const string Footer = "MinerBot by 24kConnect - Version 2.0";

        // Discord.Net's EmbedBuilder.MaxTitleLength, which the post cuts each name to
        const int TitleLimit = 256;

        // The new-armor post with only this armor in it. Other armor approved before the bot's next five-minute check
        // shares the message, in the database's order.
        public static EmbedPreview NewArmorPost(ArmorSubmission armor) => new(
            "New Armor Styles:",
            UploadAddress,
            "```\n" + Shorten(armor.ArmorName, TitleLimit) + "\n```",
            [new EmbedField("Upload your own Armor Style", UploadAddress, false)],
            null);

        // /armor lookup's card. The bot shows a save string it has stored as it is, and otherwise works one out.
        public static EmbedPreview LookupCard(ArmorSubmission armor) => new(
            armor.ArmorName,
            null,
            $"```\n{armor.ArmorDescription}\nMade by {armor.Author}```",
            [
                new EmbedField("Armor Style", ArmorStyle(armor), false),
                new EmbedField("For Save File (Colors)", $"`{armor.ColorArray ?? ArmorColors.CreateColorArray(armor)}`", true)
            ],
            ArmorRules.IsAllowedScreenshot(armor.ArmorScreenshot) ? armor.ArmorScreenshot : null);

        // FormatArmorStyle: the helmet and the skin, then each colour on a line of its own, as the bot reads them
        static string ArmorStyle(ArmorSubmission armor) =>
            "```\n" +
            $"Helmet Type: {armor.ArmorChoice}\n\n" +
            $"Armor Skin Type: {armor.ArmorSkin}\n\n" +
            "Armor Colors:\n" +
            Row("HELMET", armor.Helmet) +
            Row("VISOR", armor.Visor) +
            Row("TORSO", armor.Torso) +
            Row("SHOULDERS", armor.Shoulders) +
            Row("BICEPS", armor.Biceps) +
            Row("FOREARMS", armor.Forearms) +
            Row("HANDS", armor.Hands) +
            Row("PELVIS", armor.Pelvis) +
            Row("THIGHS", armor.Thighs) +
            Row("LEGS", armor.Legs) +
            Row("BOOTS", armor.Boots) +
            Row("SHIELDS", armor.Shields) +
            Row("OTHER", armor.Other) +
            "```";

        // The bot reads an empty colour as 0
        static string Row(string part, int? color) => part.PadRight(13) + (color ?? 0).ToString(CultureInfo.InvariantCulture) + "\n";

        // shortenTextForEmbed: longer text loses its end to three dots, never half a character
        internal static string Shorten(string? text, int limit)
        {
            if (text == null || text.Length <= limit)
                return text ?? "";

            var length = limit - 3;

            if (char.IsHighSurrogate(text[length - 1]))
                length--;

            return text[..length] + "...";
        }
    }

    public enum DiscordTextKind
    {
        // No backtick: shown as it is
        Plain,
        // ```, a line end, the text and ```, which Discord draws as one block. A single or double backtick inside shows as
        // it is, because only three in a row end the block.
        CodeBlock,
        // Exactly `, the text and `
        InlineCode,
        // Backticks the bot didn't put there, which end its code early, so Discord draws the rest some other way
        Broken
    }

    // How Discord draws one of the bot's strings, for the four shapes the bot's armor messages use. Anything else, which only
    // a backtick in the submitter's text makes, is shown as it is with a note, rather than guessed at.
    public sealed record DiscordText(DiscordTextKind Kind, string Text)
    {
        public static DiscordText Of(string value)
        {
            if (value.Length >= 7 && value.StartsWith("```\n", StringComparison.Ordinal) && value.EndsWith("```", StringComparison.Ordinal))
            {
                var inner = value[4..^3];

                // Discord ends the block at the first three backticks after the opening ones. A backtick at the end of the
                // text, as an author's can be, makes the first of those three, and the closing one is left over.
                if (!inner.Contains("```", StringComparison.Ordinal) && !inner.EndsWith('`'))
                    return new(DiscordTextKind.CodeBlock, inner.EndsWith('\n') ? inner[..^1] : inner);
            }

            if (value.Length >= 2 && value[0] == '`' && value[^1] == '`' && !value[1..^1].Contains('`'))
                return new(DiscordTextKind.InlineCode, value[1..^1]);

            return new(value.Contains('`') ? DiscordTextKind.Broken : DiscordTextKind.Plain, value);
        }

        // Raw, as Discord shows it, but encoded and isolated in a bdi, so text that turns itself around stays inside it.
        // Revealing hidden characters is left to the details above the previews.
        public IHtmlContent Html()
        {
            var html = new HtmlContentBuilder();

            return Kind switch
            {
                DiscordTextKind.CodeBlock => html.AppendHtml("<pre class=\"embed-code\"><bdi>").Append(Text).AppendHtml("</bdi></pre>"),
                DiscordTextKind.InlineCode => html.AppendHtml("<code><bdi>").Append(Text).AppendHtml("</bdi></code>"),
                DiscordTextKind.Broken => html.AppendHtml("<pre class=\"embed-raw\"><bdi>").Append(Text).AppendHtml("</bdi></pre>")
                    .AppendHtml("<p class=\"embed-note\">").Append(BrokenNote).AppendHtml("</p>"),
                _ => html.AppendHtml("<bdi>").Append(Text).AppendHtml("</bdi>")
            };
        }

        internal const string BrokenNote = "Backticks the bot didn't put there end its code early, so Discord won't draw this the way the bot meant. It's shown as the bot sends it.";
    }
}
