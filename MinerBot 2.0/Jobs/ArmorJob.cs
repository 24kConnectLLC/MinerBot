using DataAccessLibrary.Armor.Models;
using MinerBot_2._0.Attributes;
using MinerBot_2._0.Services;
using MinerBot_2._0.Extensions;
using System.Globalization;

namespace MinerBot_2._0.Jobs
{
    public class ArmorJob
    {
        // Dependency Injection will fill this value in for us
        private readonly ArmorService _ArmorService;

        // Constructor injection is also a valid way to access the dependencies
        public ArmorJob(ILogger<ArmorJob> logger, IServiceProvider services)
        {
            _ArmorService = services.GetRequiredService<ArmorService>();
        }

        // Its detail says how many it added, which the owner's message after a dashboard request shows
        [Job("0 * * * *")] // Every Hour
        public async Task<string> AddColorArrays()
        {
            var added = 0;

            foreach (var armor in await _ArmorService.GetArmorList())
            {
                if (armor.ColorArray == null)
                {
                    await AddColorArray(armor);
                    added++;
                }
            }

            return string.Create(CultureInfo.InvariantCulture, $"added colour codes to {added} armor {(added == 1 ? "style" : "styles")}");
        }

        //[Job("* * * * *")] // Every Hour
        //public async Task FixScreenshotLinks()
        //{
        //    foreach (var armor in await _ArmorService.GetArmorList())
        //    {
        //        await FixScreenshotLink(armor);
        //    }
        //}

        public async Task AddColorArray(ArmorStyle armor)
        {
            await _ArmorService.UpdateColorArray(armor.ID, armor.CreateColorArray());
        }

        public async Task FixScreenshotLink(ArmorStyle armor)
        {
            armor.ArmorScreenshot = $"https://murderminershub.com/wp-content/uploads/ArmorPreviews/{armor.ArmorName.Replace(" ","-")}.png";
            await _ArmorService.UpdateArmor(armor);
        }
    }
}
