namespace MinerBot_2._0.Posting
{
    // What the notification run keeps for the whole run of the bot, a singleton, since JobHandler makes the job anew for each
    // run: whether this process has looked for an older bot's runs, which it does once, at its first run that loads the
    // record, and the sources that still take a new baseline for them, each until a good read of it; and when Discord last
    // answered one of the posts' requests, which the look goes by past a message's hour.
    public sealed class PostingProcess
    {
        readonly Lock gate = new();
        readonly Dictionary<string, DateTime> olderBot = new(StringComparer.Ordinal);
        bool looked;
        DateTime? answered;

        public bool LookedForOlderBot
        {
            get
            {
                lock (gate)
                    return looked;
            }
        }

        // What the look found: each source an older bot posted for, with the last check of it by this bot
        public void FoundOlderBot(IReadOnlyDictionary<string, DateTime> sources)
        {
            lock (gate)
            {
                foreach (var (source, since) in sources)
                    olderBot[source] = since;

                looked = true;
            }
        }

        // The sources that still take a new baseline, with the time since which the older bot posted
        public IReadOnlyDictionary<string, DateTime> OlderBot
        {
            get
            {
                lock (gate)
                    return new Dictionary<string, DateTime>(olderBot, StringComparer.Ordinal);
            }
        }

        // Once MinerBot's database has taken a source's new baseline
        public void Rebaselined(IEnumerable<string> sources)
        {
            lock (gate)
            {
                foreach (var source in sources)
                    olderBot.Remove(source);
            }
        }

        // Discord answered a post's send or a read of a channel's messages with something of its own, by the bot's clock: the
        // message, the page of messages, or a refusal other than a rate limit or a refused token
        public void Answered(DateTime at)
        {
            lock (gate)
            {
                if (answered == null || at > answered)
                    answered = at;
            }
        }

        // When Discord last answered one of the posts' requests, as far as this process knows: not after a start, until it
        // does. The look takes it as it begins, and goes by that to its end.
        public DateTime? LastAnswered
        {
            get
            {
                lock (gate)
                    return answered;
            }
        }

        // Whether that answer came since then: never after a start, until Discord answers
        public static bool AnsweredSince(DateTime? answered, DateTime since) => answered >= since;
    }
}
