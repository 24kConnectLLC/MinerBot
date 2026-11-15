namespace MinerBot_2._0.Models
{
    // A job's own failure, in the bot's own wording, which JobHandler records as the run's detail. Any other exception is
    // recorded by its type only, because a library's message can hold anything.
    public sealed class JobFailedException(string detail) : Exception(detail);
}
