namespace DataAccessLibrary.Tests.Database
{
    // Every test that needs MariaDB is in this collection, so the whole run starts one container, and the tests take
    // turns with the tables
    [CollectionDefinition("Database", DisableParallelization = true)]
    public sealed class DatabaseCollection : ICollectionFixture<MariaDbFixture>
    {
    }
}
