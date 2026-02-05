using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataAccessLibrary.Armor.Models
{
    public class ArmorStyle
    {
        public int ID { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Author { get; set; }
        public string ArmorName { get; set; }
        public string ArmorDescription { get; set; }
        public string ArmorScreenshot { get; set; }
        public string ArmorChoice { get; set; }
        public string ArmorSkin { get; set; }
        public byte Helmet { get; set; }
        public byte Visor { get; set; }
        public byte Torso { get; set; }
        public byte Shoulders { get; set; }
        public byte Biceps { get; set; }
        public byte Forearms { get; set; }
        public byte Hands { get; set; }
        public byte Pelvis { get; set; }
        public byte Thighs { get; set; }
        public byte Legs { get; set; }
        public byte Boots { get; set; }
        public byte Shields { get; set; }
        public byte Other { get; set; }
        public string ColorArray { get; set; }
    }
}
