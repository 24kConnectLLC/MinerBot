using System.Text;
using Microsoft.AspNetCore.Html;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Armor
{
    public enum TextPart
    {
        Plain,
        Hidden,
        Mention,
        Link,
        Backtick
    }

    // A piece of text someone else wrote. A hidden piece is one character that shows nothing or turns the text around,
    // written as its code, such as [U+200B], with its name.
    public sealed record TextPiece(string Text, TextPart Part, string? Name = null);

    // Submitter text, cut into pieces so the page can mark what a reviewer has to see: hidden characters as their codes,
    // Discord's mentions and links, and backticks, which can end the code blocks the bot shows text in. One look at the
    // whole text decides, so a style selector or a joiner is judged with the characters beside it, as
    // TextInspector.Reveal judges it, and the pieces put together are Reveal's text.
    internal static class RevealedText
    {
        public static IReadOnlyList<TextPiece> Pieces(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return [];

            var findings = TextInspector.Inspect(text);
            var parts = new TextPart[text.Length];

            // Links first and mentions over them, so a mention inside a masked link is still marked as one
            foreach (var link in findings.Links)
                Array.Fill(parts, TextPart.Link, link.Index, link.Text.Length);

            foreach (var mention in findings.Mentions)
                Array.Fill(parts, TextPart.Mention, mention.Index, mention.Text.Length);

            // Over both, since one inside a link still ends a code block
            for (var index = text.IndexOf('`'); index >= 0; index = text.IndexOf('`', index + 1))
                parts[index] = TextPart.Backtick;

            var hidden = findings.Hidden.ToDictionary(character => character.Index);
            var pieces = new List<TextPiece>();
            var run = new StringBuilder();
            var part = TextPart.Plain;

            for (var index = 0; index < text.Length;)
            {
                if (hidden.TryGetValue(index, out var character))
                {
                    EndRun();
                    pieces.Add(new TextPiece($"[{character.Code}]", TextPart.Hidden, character.Name));
                    index += character.CodePoint > 0xFFFF ? 2 : 1;
                    continue;
                }

                if (parts[index] != part)
                {
                    EndRun();
                    part = parts[index];
                }

                run.Append(text[index]);
                index++;
            }

            EndRun();
            return pieces;

            void EndRun()
            {
                if (run.Length == 0)
                    return;

                pieces.Add(new TextPiece(run.ToString(), part));
                run.Clear();
            }
        }

        // The pieces as HTML, every bit of the text encoded, inside a bdi, so even text that turns itself around can't turn
        // the page's own words around it
        public static IHtmlContent Html(string? text)
        {
            var html = new HtmlContentBuilder();
            html.AppendHtml("<bdi class=\"revealed\">");

            foreach (var piece in Pieces(text))
            {
                switch (piece.Part)
                {
                    case TextPart.Hidden:
                        html.AppendHtml("<mark class=\"hidden-char\" title=\"").Append(piece.Name ?? "").AppendHtml("\">").Append(piece.Text).AppendHtml("</mark>");
                        break;
                    case TextPart.Mention:
                        html.AppendHtml("<mark class=\"mention\" title=\"Discord mention\">").Append(piece.Text).AppendHtml("</mark>");
                        break;
                    case TextPart.Link:
                        html.AppendHtml("<mark class=\"link\" title=\"Link\">").Append(piece.Text).AppendHtml("</mark>");
                        break;
                    case TextPart.Backtick:
                        html.AppendHtml("<mark class=\"backtick\" title=\"Backtick\">").Append(piece.Text).AppendHtml("</mark>");
                        break;
                    default:
                        html.Append(piece.Text);
                        break;
                }
            }

            return html.AppendHtml("</bdi>");
        }
    }
}
