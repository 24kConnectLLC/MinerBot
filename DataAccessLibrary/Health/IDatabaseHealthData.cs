namespace DataAccessLibrary.Health
{
    public interface IDatabaseHealthData
    {
        Task<bool> Answers(DBConnectionName database, TimeSpan limit);
    }
}
