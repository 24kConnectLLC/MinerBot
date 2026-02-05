using MinerBot_2._0.Attributes;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace MinerBot_2._0.Models
{
    public class Job
    {
        public string Name { get; set; }
        public MethodInfo MethodInfo { get; set; }
        public JobAttribute Details { get; set; }
        public ParameterInfo[] Parameters { get; set; }
        public Type DeclaringType { get; set; }
        public DateTimeOffset NextRun { get; set; }
    }
}
