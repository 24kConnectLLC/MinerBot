namespace MinerBot_2._0.Attributes
{
    [AttributeUsage(AttributeTargets.Method)]
    public class JobAttribute(string cronExpression) : Attribute
    {
        public string Cron = cronExpression;
    }
}
