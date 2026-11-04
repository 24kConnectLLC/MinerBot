namespace MinerBot_2._0.Extensions
{
    public static class UIExtensions
    {
        public static string ToName(this Enum val)
        {
            return val.ToString().Replace("_", "-");
        }
    }
}
