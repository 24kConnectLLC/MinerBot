using MinerBot_2._0.Attributes;
using System.Reflection;

namespace MinerBot_2._0.Models
{
    public class Job
    {
        public string Name { get; set; }
        public MethodInfo MethodInfo { get; set; }
        public JobAttribute Details { get; set; }
        public Type DeclaringType { get; set; }
        public DateTimeOffset NextRun { get; set; }

        // The last run, which can still be going after it was cut off, or while one asked for from the dashboard runs beside
        // the schedule, whether it was asked for, and when it started, by the bot's clock. JobHandler's lock guards them.
        public Task? Running { get; set; }
        public bool Requested { get; set; }
        public DateTimeOffset? RunningSince { get; set; }
    }
}
