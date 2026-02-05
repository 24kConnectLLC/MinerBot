using DataAccessLibrary.Armor.Models;
using Microsoft.Extensions.Caching.Memory;

namespace DataAccessLibrary.Armor
{
    public class ArmorData : DbBaseConnection, IArmorData
    {
        private readonly IMemoryCache _memoryCache;
        public ArmorData(ISQLConnectionFactory connectionFactory, IMemoryCache cache) : base(connectionFactory, DBConnectionName.Armor)
        {
            _memoryCache = cache;
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
                if (timeSpan == null)
                    timeSpan = TimeSpan.FromMinutes(1);

                output = await GetAllArmorNames();
                _memoryCache.Set(key, output, timeSpan);
            }

            return output;
        }

        // Redis Caching
        //public async Task<IEnumerable<string>> GetArmorListNamesRCache(HelmentType helmetChoice, int page = -1)
        //{
        //    string recordKey = "ArmorNameList_" + DateTime.Now.ToString("yyyyMMdd_hhmm");

        //    var armors = await _cache.GetRecordAsync<IEnumerable<string>>(recordKey);

        //    if (armors == null)
        //    {
        //        armors = await GetArmorListNames(helmetChoice, page);

        //        await _cache.SetRecordAsync(recordKey, armors, TimeSpan.FromMinutes(5));
        //    }

        //    return armors;
        //}

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

            return SaveData(sql, new { armor });
        }
    }
}
