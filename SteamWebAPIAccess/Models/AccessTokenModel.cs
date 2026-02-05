using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SteamWebAPIAccess.Models
{
    public class AccessTokenModel
    {
        public string iss { get; set; }
        public string sub { get; set; }
        public string[] aud {  get; set; }
        public long exp {  get; set; }
        public long nbf { get; set; }
        public long iat { get; set; }
        public string jti { get; set; }
        public long oat { get; set; }
        public long rt_exp { get; set; }
        public long per {  get; set; }
        public string ip_subject { get; set; }
        public string ip_confirmer { get; set; }
    }
}
