namespace DataAccessLibrary.Requests.Models
{
    // A request an earlier run took and never finished, which the sweep marked unknown, for its event
    public sealed class SweptRequestRow
    {
        public ulong id { get; set; }
        public string kind { get; set; } = "";
    }
}
