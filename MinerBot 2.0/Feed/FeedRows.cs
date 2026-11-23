using DataAccessLibrary.Feed;
using DataAccessLibrary.Feed.Models;
using Steam.Models;
using SteamWebAPIAccess;
using SteamWebAPIAccess.Models;

namespace MinerBot_2._0.Feed
{
    // What the bot read from Steam, made into the rows of the website feed. A website page isn't a Discord embed, so
    // nothing here is escaped for Discord or cut to Discord's sizes: the website escapes what it shows.
    internal static class FeedRows
    {
        // A lobby as the bot's own lobby list shows it, without the words the list puts in for what isn't known: a host
        // without a name, a map without one and modes that would be a guess are empty, for the page to word itself
        public static FeedLobby Lobby(LobbyInfo lobby)
        {
            // What the list takes out of a host's name: control characters, the controls that reorder text and piles of marks
            var hostName = UIUtils.cleanTextForEmbed(lobby.HostName ?? "");

            return new FeedLobby
            {
                lobby_id = lobby.LobbyId,
                owner_id = lobby.OwnerID,
                host_name = string.IsNullOrWhiteSpace(hostName) ? "" : Cut(hostName, WebsiteFeedData.HostNameLimit),
                host_kind = lobby.HostKind switch
                {
                    LobbyHostKind.Gamenight => FeedHostKind.Gamenight,
                    LobbyHostKind.Official => FeedHostKind.Official,
                    _ => FeedHostKind.Player
                },
                map_name = string.IsNullOrWhiteSpace(lobby.MapName) ? "" : Cut(lobby.MapName, WebsiteFeedData.MapNameLimit),

                // For a version whose layout isn't known exactly, the modes would be guesses
                modes = string.IsNullOrWhiteSpace(lobby.Modes) || !GameVersions.ModesTrusted(lobby.VersionNumber) ? "" : Cut(lobby.Modes, WebsiteFeedData.ModesLimit),
                version_number = lobby.VersionNumber,
                version_label = GameVersions.Find(lobby.VersionNumber)?.Branch ?? "",
                is_current = lobby.VersionNumber == GameVersions.Current,
                players = Count(lobby.CurrentPlayers),
                max_players = Count(lobby.MaxPlayers)
            };
        }

        // The maps of a read that can be written, in Steam's order. Steam's reader leaves a missing time as the default,
        // year 1, and the feed's removal rule goes by the updates of the read's last maps, so a map without its times is
        // left out.
        public static List<FeedMap> Maps(IEnumerable<PublishedFileDetailsModel> maps) =>
            maps.Where(map => map.TimeCreated != default && map.TimeUpdated != default).Select(Map).ToList();

        public static FeedMap Map(PublishedFileDetailsModel map)
        {
            var previewUrl = map.PreviewUrl?.OriginalString ?? "";

            return new FeedMap
            {
                file_id = map.PublishedFileId,
                title = Cut((map.Title ?? "").Trim(), WebsiteFeedData.TitleLimit),
                description = Description(map.Description),

                // A link cut short would be another link, so one that doesn't fit is left out
                preview_url = Length(previewUrl) > WebsiteFeedData.PreviewUrlLimit ? "" : previewUrl,
                creator_id = map.Creator,
                tags = Tags(map.Tags),
                file_size = map.FileSize,
                subscriptions = Count(map.Subscriptions),
                lifetime_subscriptions = Count(map.LifetimeSubscriptions),
                favorited = Count(map.Favorited),
                lifetime_favorited = Count(map.LifetimeFavorited),
                views = Count(map.Views),

                // Steam's reader gives UTC without saying so
                time_created = DateTime.SpecifyKind(map.TimeCreated, DateTimeKind.Utc),
                time_updated = DateTime.SpecifyKind(map.TimeUpdated, DateTimeKind.Utc)
            };
        }

        // The author's text without its BBCode, as the map embeds take it out, but not escaped for Discord. A description
        // with nothing left, such as one that was only a picture, is none.
        internal static string? Description(string? description)
        {
            if (description == null)
                return null;

            var plain = Cut(WorkshopText.StripBBCode(description), WebsiteFeedData.DescriptionLimit).TrimEnd();

            return string.IsNullOrWhiteSpace(plain) ? null : plain;
        }

        // The map's tags with commas between them and no spaces around them. The tags that fit the column are kept whole,
        // in Steam's order, and a tag that holds a comma is left out, since it would read as two.
        internal static string Tags(IList<string>? tags)
        {
            var kept = new List<string>();
            var length = 0;

            foreach (var tag in (tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()))
            {
                if (tag.Contains(','))
                    continue;

                var added = Length(tag) + (kept.Count == 0 ? 0 : 1);

                if (length + added > WebsiteFeedData.TagsLimit)
                    break;

                kept.Add(tag);
                length += added;
            }

            return string.Join(',', kept);
        }

        // At most that many characters, counted as MariaDB and the website count them, so a character outside the Basic
        // Multilingual Plane counts once and is kept whole or left out
        internal static string Cut(string text, int maxCharacters)
        {
            var end = 0;

            for (var count = 0; count < maxCharacters && end < text.Length; count++)
                end += char.IsSurrogatePair(text, end) ? 2 : 1;

            return end >= text.Length ? text : text[..end];
        }

        static int Length(string text) => text.EnumerateRunes().Count();

        // The columns are unsigned and narrower than Steam's numbers, so a number outside one is held at its end
        static ushort Count(int players) => (ushort)Math.Clamp(players, 0, ushort.MaxValue);

        static uint Count(ulong number) => (uint)Math.Min(number, uint.MaxValue);
    }
}
