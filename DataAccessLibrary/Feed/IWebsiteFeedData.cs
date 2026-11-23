using DataAccessLibrary.Feed.Models;

namespace DataAccessLibrary.Feed
{
    public interface IWebsiteFeedData
    {
        Task MarkChecked(string feed, DateTime at);
        Task<int> ReplaceLobbies(IReadOnlyList<FeedLobby> lobbies, DateTime at);
        Task<int> SaveMaps(IReadOnlyList<FeedMap> maps, DateTime at);
    }
}
