namespace DataAccessLibrary
{
    // MinerBot's tables hold UTC, and MySql.Data reads DATETIME and DATE back with no kind, which .NET takes as local time
    // as soon as anything converts it. The read models the dashboard's pages get mark each time UTC as Dapper sets it, so
    // no page has to remember to.
    internal static class UtcTimes
    {
        public static DateTime Mark(DateTime time) => DateTime.SpecifyKind(time, DateTimeKind.Utc);

        public static DateTime? Mark(DateTime? time) => time is { } value ? Mark(value) : null;
    }
}
