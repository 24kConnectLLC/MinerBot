namespace DataAccessLibrary
{
    // One page of a list, and how many rows the whole list has
    public sealed record Page<T>(IReadOnlyList<T> Rows, long Total);
}
