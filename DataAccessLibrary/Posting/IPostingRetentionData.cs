namespace DataAccessLibrary.Posting
{
    public interface IPostingRetentionData
    {
        Task<int> TotalDeliveryDays(DateTime from, DateTime to);
        Task<int> DeleteDeliveries(DateTime before, int take);
        Task<int> DeleteSettledQueueRows(DateTime before, int take);
        Task<int> DeleteSeenLobbies(DateTime before, int take);
        Task<int> DeleteLobbySamples(DateTime before, int take);
    }
}
