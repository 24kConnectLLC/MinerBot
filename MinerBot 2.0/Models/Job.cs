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

        // The last run, which can still be going after it was cut off
        public Task? Running { get; set; }
    }
}
