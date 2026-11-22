using DataAccessLibrary.Dashboard.Models;

namespace DataAccessLibrary.Dashboard
{
    // What the Sessions and security page reads to check the dashboard's own database login and MinerBot's database,
    // as that login. Reads only.
    public interface IDashboardChecks
    {
        Task<IReadOnlyList<MigrationInfo>> ListMigrations();
        Task<OwnRights> ListOwnRights();
    }
}
