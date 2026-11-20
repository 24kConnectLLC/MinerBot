using MinerBot.Shared.Steam;
using MinerBot.Shared.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace MinerBot.Shared.Requests
{
    // What a request must be before the dashboard signs it, before DataAccessLibrary saves it, and again once the bot has
    // verified it. Each problem is a few words that never quote the request's own text, such as "the status text has a
    // link", because the bot writes them into its result, which the dashboard shows.
    public static class RequestRules
    {
        // What bot_commands holds, counted in code points the way MariaDB counts them
        public const int KindLimit = 40;
        public const int RoleLimit = 20;
        public const int NameLimit = 100;
        public const int ArgumentLimit = 200;

        // A pause's reason fills the argument's column
        public const int ReasonLimit = 200;

        // The plan's 128, counted in UTF-16 characters as Discord counts, so an emoji outside the Basic Multilingual Plane
        // counts twice
        public const int StatusTextLimit = 128;

        // Words in small letters joined by one dot, such as posts.pause, which also fits an event's kind and an audit action
        static readonly Regex KindForm = new(@"^[a-z_]+\.[a-z_]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        static readonly Regex RoleForm = new(@"^[a-z_]+\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // The characters Unicode always breaks a line at that aren't control characters
        static readonly char[] Separators = ['\u2028', '\u2029'];

        // Han and kana, halfwidth kana included, and the ideographic iteration marks. Chinese and Japanese end a sentence
        // with a full stop of their own and no space after it, so one of those next to them ends a sentence rather than
        // joins names.
        const string HanOrKana = @"[\u3005-\u3007\u3040-\u30FF\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF\uFF66-\uFF9F]";

        // The characters that read as a dot: the full stop, the one dot leader and the small full stop, and the fullwidth,
        // ideographic and halfwidth ideographic full stops, which a browser takes for dots too. Those three count only where
        // neither side is Han or kana.
        const string Dot = @"(?:[.\u2024\uFE52]|(?<!" + HanOrKana + @")[\uFF0E\u3002\uFF61](?!" + HanOrKana + @"))";

        // The letter or digit an address can't have next to it, as v1.2.3.4 has, with the marks after it: a mark counts
        // with the character before it, so an address straight after an emoji that ends with a style selector, such as an
        // arrow, still counts. A letter or digit an enclosing mark closes in, as in a keycap, isn't one, and nor is the
        // information sign, which Unicode counts as a letter: both show as emoji, not as part of a word with an address.
        const string NoWordBefore = @"(?<![\p{L}\p{N}-[\u2139]][\p{M}-[\p{Me}]]*)";
        const string NoWordAfter = @"(?!\p{M}*[\p{L}\p{N}-[\u2139]](?![\p{M}-[\p{Me}]]*\p{Me}))";

        // An address written without a scheme or www, such as x.com/minerbot or dsc.gg/minerbot, which the shared link
        // pattern leaves to Discord: names of letters, marks, digits and hyphens joined by dots, ending in a name that starts
        // with a letter and has two or more, with no letter or digit straight after it; or, as an IP address is written,
        // four numbers of one to three digits joined by dots, with no letter or digit on either side, such as
        // 203.0.113.5/nitro, also next to an emoji (above). So a version such as 1.2.3.4 counts too, though v1.2.3.4
        // doesn't. Letters and digits of any script, so fullwidth and lookalike ones count, and marks straight after a dot
        // or a digit, such as a style selector, which draws nothing there and which TextInspector lets through after a
        // digit, as in a keycap. A status text is plain text wherever it shows, so an address a reader can type out is as
        // much a link there as one Discord makes clickable. Two words joined by a dot, such as Discord.Net, count too:
        // telling them from an address would take a list of every top-level domain. The pattern is also matched against
        // the text's browser form, below.
        static readonly Regex Address = new(@"[\p{L}\p{M}\p{N}-]+(?:" + Dot + @"[\p{L}\p{M}\p{N}-]+)*" + Dot + @"\p{M}*\p{L}[\p{L}\p{M}]+" + NoWordAfter
            + @"|" + NoWordBefore + @"(?:\p{Nd}\p{M}*){1,3}(?:" + Dot + @"\p{M}*(?:\p{Nd}\p{M}*){1,3}){3}" + NoWordAfter,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // Everything checked before a request is signed or saved: its form, a kind on the list, a role the kind allows, an
        // expiry the kind allows, and the details
        public static string? Problem(RequestFields fields)
        {
            if (FormProblem(fields) is { } form)
                return form;

            var kind = RequestKinds.Find(fields.Kind);

            if (kind == null)
                return "its kind isn't on the request list";

            if (!kind.Allows(fields.ActorRole))
                return $"only {kind.WhoMayAsk} can ask for it";

            if (!kind.FitsExpiry(fields.CreatedAt, fields.ExpiresAt))
                return $"it lasts longer than its kind's {kind.Expiry.TotalMinutes:0} minutes";

            return DetailsProblem(fields);
        }

        // Whether the row is in the form the dashboard writes: a key of 32 hexadecimal digits in small letters, a kind and a
        // role shaped like the list's words, an actor, a name of 1 to 100 characters with no control characters, a text of
        // at most 200, times that can be signed, and an expiry after the time it was made. The kind and the role are only
        // looked up on the list once the signature has passed.
        public static string? FormProblem(RequestFields fields)
        {
            if (fields == null)
                return "it has no fields";

            if (fields.RequestKey is not { Length: 32 } key || !key.All(char.IsAsciiHexDigitLower))
                return "its key isn't 32 hexadecimal digits in small letters";

            if (fields.Kind is not { Length: <= KindLimit } kindWord || !KindForm.IsMatch(kindWord))
                return "its kind isn't a word of the request list's form";

            if (fields.ActorRole is not { Length: <= RoleLimit } role || !RoleForm.IsMatch(role))
                return "its role isn't a word of the request list's form";

            if (fields.ActorId == 0)
                return "it names no one who asked";

            if (string.IsNullOrEmpty(fields.ActorName))
                return "the name of who asked is empty";

            if (CodePoints(fields.ActorName) > NameLimit)
                return $"the name of who asked is longer than {NameLimit} characters";

            if (fields.ActorName.Any(char.IsControl))
                return "the name of who asked has a control character";

            if (HasLoneSurrogate(fields.ActorName))
                return "the name of who asked has a broken character";

            if (fields.Argument != null && CodePoints(fields.Argument) > ArgumentLimit)
                return $"its text is longer than {ArgumentLimit} characters";

            if (fields.Argument != null && HasLoneSurrogate(fields.Argument))
                return "its text has a broken character";

            if (TimeProblem(fields.CreatedAt) is { } made)
                return $"the time it was made {made}";

            if (TimeProblem(fields.ExpiresAt) is { } expires)
                return $"its expiry {expires}";

            if (fields.ExpiresAt <= fields.CreatedAt)
                return "it expires before it was made";

            return null;
        }

        // Whether the server, the channel and the text fit the kind's rules, for "This request's details don't fit its kind:
        // <rule>."
        public static string? DetailsProblem(RequestFields fields)
        {
            var kind = RequestKinds.Find(fields?.Kind);

            if (fields == null || kind == null)
                return "its kind isn't on the request list";

            if (kind.NeedsServer && fields.GuildId is null or 0UL)
                return "it needs a server";

            if (!kind.NeedsServer && fields.GuildId != null)
                return "it takes no server";

            if (kind.NeedsChannel && fields.ChannelId is null or 0UL)
                return "it needs a channel";

            if (!kind.NeedsChannel && fields.ChannelId != null)
                return "it takes no channel";

            return kind.Argument switch
            {
                RequestArgument.Reason => ReasonProblem(fields.Argument),
                RequestArgument.StatusText => StatusTextProblem(fields.Argument),
                RequestArgument.CustomLinkName => CustomLinkNameProblem(fields.Argument),
                _ => fields.Argument == null ? null : "it takes no text"
            };
        }

        // Why posts are paused: 1 to 200 characters on one line, with nothing hidden or turned around and no white space at
        // either end
        public static string? ReasonProblem(string? reason)
        {
            if (string.IsNullOrEmpty(reason))
                return "the reason is empty";

            if (CodePoints(reason) > ReasonLimit)
                return $"the reason is longer than {ReasonLimit} characters";

            if (HasLoneSurrogate(reason))
                return "the reason has a broken character";

            if (HasControlOrLineBreak(reason))
                return "the reason has a control character or a line break";

            if (Inspect(reason).HasHidden)
                return "the reason has a character that shows nothing or changes the text's direction";

            if (char.IsWhiteSpace(reason[0]) || char.IsWhiteSpace(reason[^1]))
                return "the reason starts or ends with white space";

            return null;
        }

        // The bot's status text, which every server shows: null for the default, otherwise 1 to 128 characters on one line,
        // with nothing hidden or turned around, no mention and no link, and no white space at either end. Emoji are fine.
        public static string? StatusTextProblem(string? text)
        {
            if (text == null)
                return null;

            if (text.Length == 0)
                return "the status text is empty";

            if (text.Length > StatusTextLimit)
                return $"the status text is longer than {StatusTextLimit} characters";

            // U+FFFE too, the byte order mark turned around, which Unicode never assigns and .NET's normalization refuses as it
            // does a lone surrogate, so the browser form below can't throw
            if (HasLoneSurrogate(text) || text.Contains('\uFFFE'))
                return "the status text has a broken character";

            if (HasControlOrLineBreak(text))
                return "the status text has a control character or a line break";

            var findings = Inspect(text);

            if (findings.HasHidden)
                return "the status text has a character that shows nothing or changes the text's direction";

            if (findings.Mentions.Count > 0)
                return "the status text has Discord's mention syntax";

            if (findings.Links.Any(HoldsLink) || Address.IsMatch(text) || Address.IsMatch(BrowserForm(text)))
                return "the status text has a link";

            if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
                return "the status text starts or ends with white space";

            return null;
        }

        // The name in a custom Steam profile link, by SteamIds' own rule
        public static string? CustomLinkNameProblem(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return "the custom link's name is empty";

            if (name.Length > SteamIds.CustomLinkNameLimit)
                return $"the custom link's name is longer than {SteamIds.CustomLinkNameLimit} characters";

            if (!SteamIds.IsCustomLinkName(name))
                return "the custom link's name has a character other than the letters A to Z, digits, _ and -";

            return null;
        }

        static string? TimeProblem(DateTime time)
        {
            if (time.Kind == DateTimeKind.Local)
                return "is local, not UTC";

            if (time.Ticks % TimeSpan.TicksPerMillisecond != 0)
                return "goes past the millisecond";

            return null;
        }

        static bool HasControlOrLineBreak(string text) => text.Any(char.IsControl) || text.IndexOfAny(Separators) >= 0;

        // The flags of England, Scotland and Wales, the only ones of their kind Unicode recommends: a black flag, tag
        // letters naming the region, and a cancel tag, which show as one flag, as Discord's emoji picker gives them. Only
        // these whole sequences, so any other tag character still counts as hidden, and no text can hide in tags.
        static readonly string[] TagFlags =
        [
            "\U0001F3F4\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F",
            "\U0001F3F4\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F",
            "\U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F"
        ];

        // Any one of those flags, found in one pass over the text as it was given, so the black flag that one becomes can't
        // make another flag with the tags after it, as England's flag would with Scotland's tags after it
        static readonly Regex TagFlag = new(string.Join('|', TagFlags.Select(Regex.Escape)), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        // What TextInspector finds, with each of those flags read as the black flag alone
        static TextFindings Inspect(string text) => TextInspector.Inspect(TagFlag.Replace(text, "\U0001F3F4"));

        static readonly char[] MaskedLinkMarks = ['[', ']', '(', ')'];

        // Discord's masked-link form, [label](target), counts only with a link in it. A status text is plain text
        // wherever it shows, so a clan tag before a region, such as [MM](EU), shows as it's written. The label and the
        // target are inspected again without the brackets, and Address sees the whole text.
        static bool HoldsLink(TextMatch link) =>
            link.Text[0] != '[' || TextInspector.Inspect(string.Join(' ', link.Text.Split(MaskedLinkMarks))).Links.Count > 0;

        // The dots the pattern knows, which the text's browser form keeps as they are
        static readonly char[] Dots = ['.', '\u2024', '\uFE52', '\uFF0E', '\u3002', '\uFF61'];

        // The text as a browser reads an address in it, in compatibility form (UTS #46), where a letter or digit drawn in
        // another style becomes the plain one. The pattern alone misses some of those: .NET matches a character outside the
        // Basic Multilingual Plane, such as a mathematical bold letter, as two halves that aren't letters, and Unicode counts
        // a circled letter as a symbol. The dots the pattern knows stay as they are, so its own rules for them hold: the
        // fullwidth full stop, which compatibility form makes a full stop, still counts only where neither side is Han or
        // kana. Normalize throws on a lone surrogate and on U+FFFE, which were both refused before this.
        static string BrowserForm(string text)
        {
            var form = new StringBuilder(text.Length);
            var start = 0;

            for (int dot; (dot = text.IndexOfAny(Dots, start)) >= 0; start = dot + 1)
                form.Append(Compatible(text[start..dot])).Append(text[dot]);

            return form.Append(Compatible(text[start..])).ToString();
        }

        static string Compatible(string text) => text.Normalize(NormalizationForm.FormKC);

        // A half of a surrogate pair without its other half, which UTF-8 can't write
        static bool HasLoneSurrogate(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;
                else if (char.IsSurrogate(text[i]))
                    return true;
            }

            return false;
        }

        static int CodePoints(string text) => text.EnumerateRunes().Count();
    }
}
