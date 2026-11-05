using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccessLibrary.Blacklist.Models
{
    public class BlacklistModel
    {
        // The account number, the n in [U:1:n], which is what the website's table holds
        public uint steam_id { get; set; }
        public string username { get; set; }
        public string reason { get; set; }
        public DateTime? expire_date { get; set; }
        public string notes { get; set; }

        public override string ToString()
        {
            return $"{steam_id};{username};{reason};{expire_date};{notes}";
        }
    }
}
