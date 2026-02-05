using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using Discord;
using Microsoft.Extensions.Caching.Distributed;
using MinerBot_2._0.Builders;
using MinerBot_2._0.Extensions;
using ProtoBuf;
using SteamWebAPIAccess;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MinerBot_2._0.Services
{
    public class ArmorService
    {
        private readonly IArmorData _armorData;

        public int ItemsPerPage = 20;

        public ArmorService(IArmorData armorData)
        {
            _armorData = armorData;
        }

        // Create a modal interaction to have users upload their armor just like on the website.

        public async Task<List<ArmorStyle>> GetArmorList()
        {
            var allArmors = await _armorData.GetAllArmor();
            return allArmors.ToList();
        }

        public async Task<IEnumerable<string>> GetArmorListNames(HelmentType helmetChoice = HelmentType.All, int page = -1, bool cache = true)
        {
            IEnumerable<string> armors;
            if (helmetChoice == HelmentType.All)
            {
                if (cache)
                    armors = await _armorData.GetAllArmorNamesCache(TimeSpan.FromMinutes(5));
                else
                    armors = await _armorData.GetAllArmorNames();
            }
            else
                armors = await _armorData.GetArmorNamesByHelmet(helmetChoice.ToName());

            if (page != -1)
                armors = armors.Skip(page * ItemsPerPage).Take(ItemsPerPage + page);
            
            return armors;
        }

        public async Task<string> ArmorListNamesFormated(HelmentType helmetChoice, IEnumerable<string> armors, int page)
        {
            if (armors == null)
                armors = await GetArmorListNames(helmetChoice, page);
            string allArmors = "";

            foreach (var armor in armors)
            {
                allArmors += armor + "\n";
            }
            return allArmors;
        }

        // Ideas:
        // - Render Each armor miner by itself without the menu. As a .png. Maybe it can be a debug option for people. Or can render from here. But mods won't show.
        public async Task<ArmorStyle> GetArmorStyle(string name)
        {
            return await _armorData.GetArmorStyle(name);
        }

        public async Task<string> FormatArmorStyle(ArmorStyle armor)
        {
            return $"```\n" +
            $"Helmet Type: {armor.ArmorChoice}\n\n" +
            $"Armor Skin Type: {armor.ArmorSkin}\n\n" +
            $"Armor Colors:\n" +
            $"{rowSpaced("HELMET", armor.Helmet.ToString())}\n" +
            $"{rowSpaced("VISOR", armor.Visor.ToString())}\n" +
            $"{rowSpaced("TORSO", armor.Torso.ToString())}\n" +
            $"{rowSpaced("SHOULDERS", armor.Shoulders.ToString())}\n" +
            $"{rowSpaced("BICEPS", armor.Biceps.ToString())}\n" +
            $"{rowSpaced("FOREARMS", armor.Forearms.ToString())}\n" +
            $"{rowSpaced("HANDS", armor.Hands.ToString())}\n" +
            $"{rowSpaced("PELVIS", armor.Pelvis.ToString())}\n" +
            $"{rowSpaced("THIGHS", armor.Thighs.ToString())}\n" +
            $"{rowSpaced("LEGS", armor.Legs.ToString())}\n" +
            $"{rowSpaced("BOOTS", armor.Boots.ToString())}\n" +
            $"{rowSpaced("SHIELDS", armor.Shields.ToString())}\n" +
            $"{rowSpaced("OTHER", armor.Other.ToString())}\n" +
            "```";

            string rowSpaced(string left, string right)
            {
                int width = 13;

                return left.PadRight(width) + right;
            }
        }

        public async Task<Embed> NewArmorListEmbed(List<ArmorStyle> newArmors)
        {
            string allArmors = "";
            foreach (var armor in newArmors)
            {
                allArmors += armor.ArmorName + "\n";
            }

            var embed = new EmbedBuilderTemplate()
            .WithTitle($"New Armor Styles:")
            .WithDescription($"```{allArmors}```")
            .WithFields([
            new EmbedFieldBuilder() {
                        Name = "Upload your own Armor Style",
                        Value = "https://murderminershub.com/upload-armor-style/"
                    }
            ])
            .WithUrl("https://murderminershub.com/upload-armor-style/")
            .Build();

            return embed;
        }

        public async Task<Embed> ArmorListEmbed(HelmentType helmetType, IEnumerable<string> armors = null, int page = 0)
        {
            var embed = new EmbedBuilderTemplate()
            .WithTitle($"Armor List: {helmetType.ToName()}")
            .WithDescription($"```{await ArmorListNamesFormated(helmetType, armors, page)}```")
            .WithFields([
            new EmbedFieldBuilder() {
                        Name = "Upload your own Armor Style",
                        Value = "https://murderminershub.com/upload-armor-style/"
                    }
            ])
            .WithUrl("https://murderminershub.com/upload-armor-style/")
            .Build();

            return embed;
        }

        public string FormatImageURL(string imageName, string imageType = "", bool includeSpecial = false)
        {
            if (string.IsNullOrEmpty(imageName))
                return "MinerPreview";

            // Remove whitespace in armor name so discord doesn't replace it with underscores.
            var formattedName = imageName.Replace(" ", "");

            // Also remove special characters such as !
            if (!includeSpecial)
            {
                StringBuilder sb = new StringBuilder();
                foreach (var c in formattedName)
                {
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    {
                        sb.Append(c);
                    }
                }

                if (sb.Length > 0)
                    formattedName = sb.ToString();
                else
                    formattedName = "MinerPreview";
            }

            return formattedName + imageType;
        }

        public async Task<(Embed ArmorEmbed, FileAttachment ArmorImage)> ArmorEmbed(ArmorStyle armor = null, string armorName = "Free Palestine")
        {
            if (armor == null)
                armor = await GetArmorStyle(armorName);

            var imageName = FormatImageURL(armor.ArmorName, ".png");
            var embed = new EmbedBuilderTemplate()
                .WithTitle(armor.ArmorName)
                .WithDescription($"```{armor.ArmorDescription}\nMade by {armor.Author}```")
                .WithFields([
                    new EmbedFieldBuilder{
                    Name = "Armor Style",
                    Value = FormatArmorStyle(armor).Result
                },
                new EmbedFieldBuilder{
                    Name = "For Save File (Colors)",
                    Value = $"`{(armor.ColorArray == null ? armor.CreateColorArray() : armor.ColorArray)}`",
                    IsInline = true
                }
                ])
                .WithImageUrl($"attachment://{imageName}")
                .Build();

            var image = await GetFileAttachmentAsync(FormatImageURL(armor.ArmorScreenshot, includeSpecial: true), imageName, armor.ArmorDescription);

            return (embed, image);
        }

        public async Task<FileAttachment> GetFileAttachmentAsync(string url, string fileName, string description = "")
        {
            using (HttpClient client = new HttpClient())
            {
                var response = await client.GetStreamAsync(url);
                var imageFile = new FileAttachment(response, fileName, description);
                return imageFile;
            }
        }

        public async Task<Embed> ErrorEmbed()
        {
            var embed = new EmbedBuilderTemplate()
                .WithTitle("hmmm...")
                .WithDescription("That armor is not in my armor list. Please type /armor_list to see the full armor list.")
                .WithColor(Color.Red)
                .WithFields([
                new EmbedFieldBuilder() {
                        Name = "Upload your own Armor Style",
                        Value = "https://murderminershub.com/upload-armor-style/"
                    }
                ])
                .WithUrl("https://murderminershub.com/upload-armor-style/")
                .Build();
            return embed;
        }

        public async Task<Embed> GetHelmetsEmbed()
        {
            var embed = new EmbedBuilderTemplate()
                .WithTitle($"Helmets:")
                .WithDescription($"```{String.Join("\n",Enum.GetNames<HelmentType>()).Replace("_","-")}```")
                .Build();
            return embed;
        }

        public async Task<string> UpdateArmor(ArmorStyle armorStyle)
        {
            await _armorData.UpdateArmor(armorStyle);
            return $"Sucessfully Updated Armor: {armorStyle.ID}, {armorStyle.ArmorName}";
        }

        public async Task<string> DeleteArmor(ArmorStyle armorStyle)
        {
            await _armorData.DeleteArmor(armorStyle);
            return $"Sucessfully Deleted Armor: {armorStyle.ID}, {armorStyle.ArmorName}";
        }

        public async Task<string> InsertArmor(ArmorStyle armorStyle)
        {
            await _armorData.InsertArmor(armorStyle);
            return $"Sucessfully Inserted Armor: {armorStyle.ID}, {armorStyle.ArmorName}";
        }

        public async Task<string> UpdateColorArray(int ID, string colorArray)
        {
            await _armorData.UpdateColorArray(ID, colorArray);
            return $"Sucessfully Updated Color Array: {ID}, {colorArray}";
        }
    }
}
