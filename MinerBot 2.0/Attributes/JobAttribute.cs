using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public class JobAttribute(string cronExpression) : Attribute
    {
        public string Cron = cronExpression;
    }
}
