using DataAccessLibrary.Armor.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace DataAccessLibrary.Armor
{
    public class ArmorData : DbBaseConnection, IArmorData
    {
        private readonly IMemoryCache _memoryCache;
        private readonly CacheReset _reset;
        public ArmorData(ISQLConnectionFactory connectionFactory, IMemoryCache cache, CacheReset reset) : base(connectionFactory, DBConnectionName.Armor)
        {
            _memoryCache = cache;
            _reset = reset;

            // The bot's own queries keep the connection string exactly as configured
            SendsUtf8mb4 = false;
        }

        public Task<IEnumerable<ArmorStyle>> GetAllArmor()
        {
            string sql = @"SELECT *
                            FROM ihvph_armor_styles
                            WHERE Status = 'Approved'
                            ORDER BY ArmorName";

            return LoadDataEnum<ArmorStyle, dynamic>(sql, new { });
        }

        public Task<IEnumerable<string>> GetAllArmorNames()
        {
            string sql = @"SELECT ArmorName
                            FROM ihvph_armor_styles
                            WHERE Status = 'Approved'
                            ORDER BY ArmorName";

            return LoadDataEnum<string, dynamic>(sql, new { });
        }

        public async Task<IEnumerable<string>> GetAllArmorNamesCache(TimeSpan timeSpan)
        {
            IEnumerable<string> output;

            string key = "AllArmorNames";

            output = _memoryCache.Get<IEnumerable<string>>(key);

            if (output == null)
            {
                // Before the read, so a clear that comes during it keeps this list out
                var reset = _reset.Token;

                output = await GetAllArmorNames();
                _memoryCache.Set(key, output, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeSpan }.AddExpirationToken(new CancellationChangeToken(reset)));
            }

            return output;
        }

        public Task<IEnumerable<string>> GetArmorNamesByHelmet(string helmetChoice)
        {
            string sql = @"SELECT ArmorName
                            FROM ihvph_armor_styles
                            WHERE Status = 'Approved' AND ArmorChoice = @helmetChoice
                            ORDER BY ArmorName";

            return LoadDataEnum<string, dynamic>(sql, new { helmetChoice });
        }

        public Task<ArmorStyle> GetArmorStyle(string name)
        {
            string sql = @"SELECT *
                            FROM ihvph_armor_styles
                            WHERE Status = 'Approved' AND ArmorName = @name";

            return LoadFirstData<ArmorStyle, dynamic>(sql, new { name });
        }

        public Task UpdateColorArray(int ID, string colorArray)
        {
            string sql = @"UPDATE ihvph_armor_styles
                            SET ColorArray = @colorArray
                            WHERE ID = @ID"
            ;

            return SaveData<dynamic>(sql, new { ID, colorArray });
        }

        public Task UpdateArmor(ArmorStyle armor)
        {
            string sql = @"UPDATE ihvph_armor_styles
                            SET Author = @Author, ArmorName = @ArmorName, ArmorDescription = @ArmorDescription, ArmorScreenshot = @ArmorScreenshot, ArmorChoice = @ArmorChoice,
                            ArmorSkin = @ArmorSkin, Helmet = @Helmet, Visor = @Visor, Torso = @Torso, Shoulders = @Shoulders, Biceps = @Biceps, Forearms = @Forearms,
                            Hands = @Hands, Pelvis = @Pelvis, Thighs = @Thighs, Legs = @Legs, Boots = @Boots, Shields = @Shields, Other = @Other, ColorArray = @ColorArray
                            WHERE ID = @ID";

            return SaveData(sql, armor);
        }

        public Task InsertArmor(ArmorStyle armor)
        {
            string sql = @"INSERT into ihvph_armor_styles (Author, ArmorName, ArmorDescription, ArmorScreenshot, ArmorChoice, ArmorSkin, Helmet, Visor, Torso, Shoulders, Biceps, Forearms,
                            Hands, Pelvis, Thighs, Legs, Boots, Shields, Other, ColorArray)
                            values (@Author, @ArmorName, @ArmorDescription, @ArmorScreenshot, @ArmorChoice, @ArmorSkin, @Helmet, @Visor, @Torso, @Shoulders, @Biceps, @Forearms,
                            @Hands, @Pelvis, @Thighs, @Legs, @Boots, @Shields, @Other, @ColorArray)";

            return SaveData(sql, armor);
        }

        public Task DeleteArmor(ArmorStyle armor)
        {
            string sql = @"DELETE FROM ihvph_armor_styles
                            WHERE ID = @ID";

            return SaveData(sql, new { armor.ID });
        }
    }
}
