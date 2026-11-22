using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MinerBot.Shared.Requests;
using MinerBot.Shared.Text;

namespace MinerBot.Dashboard.Requests
{
    // What someone typed to confirm a control, as the form sent it
    internal abstract record Confirmation
    {
        // The control's word, such as restart
        public sealed record Word(string? Typed) : Confirmation;

        // A server's name, as MinerBot's copy has it when the form is sent, or its ID
        public sealed record Server(string? Typed, string? Name, ulong Id) : Confirmation;

        // A status text, which is its own typing: it only has to be there, since no text is Back to the default's form
        public sealed record StatusText : Confirmation;
    }

    // The typed words that stop accidents on the controls that are hard to take back. They don't stop an attacker, who
    // types them too: the sign-in from the last 15 minutes does that. All compared on the server, whatever the form held.
    internal static partial class Confirmations
    {
        // The word each control needs typed, or null for one that needs none. Leaving a server takes its name or ID instead.
        public static string? WordFor(RequestKind kind) => kind.Word switch
        {
            RequestKinds.BotRestart => "restart",
            RequestKinds.PostsPause => "pause",
            RequestKinds.SteamReconnect => "reconnect",
            RequestKinds.SteamRenew => "renew",
            _ => null
        };

        public static bool NeedsServer(RequestKind kind) => kind.Word == RequestKinds.GuildLeave;

        // Without the white space around it, and in any case
        public static bool MatchesWord(string? typed, string word) => string.Equals(typed?.Trim(), word, StringComparison.OrdinalIgnoreCase);

        // The server's ID in digits, or its name as it reads, ignoring case: Discord names can hold characters that show
        // nothing or turn the text around, which the page shows as their codes and nobody can type. The name is taken
        // without them, or with their codes as the page shows it, which is what copying it from the page gives. A name with
        // nothing left to type takes only the ID.
        public static bool MatchesServer(string? typed, string? name, ulong id)
        {
            if (string.Equals(typed?.Trim(), id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                return true;

            var typedName = Simple(typed);
            var serverName = Simple(name);

            if (!string.IsNullOrEmpty(serverName) && string.Equals(typedName, Simple(TextInspector.Reveal(name)), StringComparison.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrEmpty(serverName) && string.Equals(typedName, serverName, StringComparison.OrdinalIgnoreCase);
        }

        // The text as it reads: in compatibility form (NFKC), without the characters TextInspector reports as showing nothing
        // or changing the direction, with each run of white space as one space, and trimmed. Null for text .NET's
        // normalization refuses, such as a lone surrogate, which then matches nothing.
        internal static string? Simple(string? text)
        {
            if (text == null)
                return null;

            string normal;

            try
            {
                normal = text.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return null;
            }

            var shown = new StringBuilder(normal.Length);
            var next = 0;

            foreach (var hidden in TextInspector.Inspect(normal).Hidden)
            {
                shown.Append(normal, next, hidden.Index - next);
                next = hidden.Index + (hidden.CodePoint > 0xFFFF ? 2 : 1);
            }

            shown.Append(normal, next, normal.Length - next);
            return WhiteSpace().Replace(shown.ToString(), " ").Trim();
        }

        [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
        private static partial Regex WhiteSpace();
    }
}
