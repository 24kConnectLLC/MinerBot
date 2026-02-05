using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerShellLibrary.Extensions
{
    public static class UIExtensions
    {
        public static string ToName(this Enum val)
        {
            return val.ToString().Replace("_", "-");
        }
    }
}
