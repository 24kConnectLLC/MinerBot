using DataAccessLibrary.Armor.Models;

namespace DataAccessLibrary.Armor
{
    public interface IArmorData
    {
        Task DeleteArmor(ArmorStyle armor);
        Task<IEnumerable<ArmorStyle>> GetAllArmor();
        Task<IEnumerable<string>> GetAllArmorNames();
        Task<IEnumerable<string>> GetAllArmorNamesCache(TimeSpan timeSpan);
        Task<IEnumerable<string>> GetArmorNamesByHelmet(string helmetChoice);
        Task<ArmorStyle> GetArmorStyle(string name);
        Task InsertArmor(ArmorStyle armor);
        Task UpdateArmor(ArmorStyle armor);
        Task UpdateColorArray(int ID, string colorArray);
    }
}