using DataAccessLibrary.Posting;

namespace MinerBot_2._0.Posting
{
    // The numbers the notification run goes by
    public static class PostingLimits
    {
        // A run reads Steam's latest five maps, as /maps latest shows them, while the maps' last good read is this recent. After
        // a gap, such as a restart that crossed a run, Steam failing or MinerBot's database down, and at every baseline, it
        // reads twenty, in at most GetData.MaxQueryPages pages of Steam's list, so the maps updated meanwhile are still in it.
        public const int LatestMaps = 5;
        public const int DeepMaps = 20;
        public static readonly TimeSpan RecentRead = TimeSpan.FromMinutes(10);

        // So a map that's updated again and again is posted once a day at most, counted on the bot's clock from when it was
        // last queued for a server
        public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

        // In a read after a gap, a map updated this long before the newest update of the read is old news: it's recorded and
        // never posted, which bounds what that read can post. A read minutes after the last has no such bound, so a map made
        // public, or shown again, long after its last update is still posted. Steam's times are only compared with Steam's.
        public static readonly TimeSpan OldNews = TimeSpan.FromHours(24);

        // An open lobby's record says when it was last seen, which its retention goes by, written again at most once this often
        public static readonly TimeSpan LobbySeenAgain = TimeSpan.FromHours(1);

        // How long an item waits for a server before it's given up there as too old to be news: a late armor post is still
        // news, a lobby opened half an hour ago isn't. By time, not runs, since the job can be asked for every minute.
        public static TimeSpan GiveUpAfter(string source) => source switch
        {
            PostSource.Armor => TimeSpan.FromDays(7),
            PostSource.Maps => TimeSpan.FromDays(2),
            PostSource.Lobbies => TimeSpan.FromMinutes(30),
            _ => throw new ArgumentException($"Only these kinds are posted: {string.Join(", ", PostSource.Posted)}", nameof(source))
        };

        // How many of Steam's latest maps a run reads: deep at a baseline, and after a gap since the maps' last good read
        public static int MapReadDepth(DateTime now, DateTime? lastGoodRead, bool baseline) =>
            !baseline && lastGoodRead is { } last && now - last < RecentRead && now >= last ? LatestMaps : DeepMaps;

        // A run claims at most this many messages, and none once it has run this long, so with each request's own limit it ends
        // well inside JobHandler's 270 s. The servers it didn't reach have the oldest rows, so they go first at the next run.
        public const int MessagesPerRun = 50;
        public static readonly TimeSpan ClaimTime = TimeSpan.FromSeconds(200);

        // How long one request to Discord, a send or a read of a channel's messages, may take before the run ends it
        // (PostingGate.Limit), whatever it waits for, Discord's answer or a rate limit's end, so one channel's long rate limit
        // never holds the other servers' posts: a send it ends counts as one Discord didn't answer. Shorter than PostingDrain's
        // wait for the post in flight, so one under way at a stop has ended by itself before that wait is over.
        public static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(15);

        // Sends Discord didn't answer that stop a run's sends: Discord isn't answering, and the rest wait for the next run
        public const int UnansweredPerRun = 2;

        // The look in a channel's history for a message whose fate is unknown. A message in doubt, or another process's still
        // sending, is looked for once it's a minute old, so one Discord made late has arrived; this process's own still
        // sending only after 5 minutes without a settle waiting in memory, which a fault alone can leave.
        public static readonly TimeSpan CheckAfter = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan StaleSending = TimeSpan.FromMinutes(5);

        // The messages read: from 5 s before the attempt, for the clocks' difference, to 2 minutes after it, a page of 100
        // at a time and at most three pages
        public static readonly TimeSpan HistoryBefore = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan HistoryAfter = TimeSpan.FromMinutes(2);
        public const int HistoryPage = 100;
        public const int HistoryPages = 3;

        // A message the bot can't look for at its third look, or an hour after its attempt, counts as sent, so it's never sent
        // twice
        public const int Looks = 3;
        public static readonly TimeSpan LookFor = TimeSpan.FromHours(1);

        // But while Discord has answered none of the posts' requests since the attempt, as in an outage of any form, a read of
        // the channel that gets no answer keeps the message waiting past that hour, up to its kind's time limit, after which
        // it's no news: armor 7 days, maps 2, lobbies the hour itself
        public static TimeSpan OutageLookFor(string source) => GiveUpAfter(source) > LookFor ? GiveUpAfter(source) : LookFor;
    }
}
