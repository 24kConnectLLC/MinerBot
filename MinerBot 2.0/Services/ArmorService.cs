using DataAccessLibrary.Armor;
using DataAccessLibrary.Armor.Models;
using Discord;
using MinerBot_2._0.Builders;
using MinerBot_2._0.Extensions;
using System.Text;

namespace MinerBot_2._0.Services
{
    public class ArmorService
    {
        private readonly IArmorData _armorData;
        private readonly HttpClient _httpClient;

        public int ItemsPerPage = 20;

        // Shorter in tests
        internal TimeSpan ScreenshotLimit = TimeSpan.FromSeconds(8);

        // Discord's limit for the description of an attachment
        const int MaxAttachmentDescriptionLength = 1024;

        public ArmorService(IArmorData armorData, HttpClient httpClient)
        {
            _armorData = armorData;
            _httpClient = httpClient;
        }

        // Discord doesn't hold an autocomplete option to its suggestions, so this can be any text
        public static bool TryParseHelmet(string name, out HelmentType helmet)
        {
            helmet = default;

            var helmetName = name.Trim().Replace("-", "_");

            // TryParse also accepts numbers and comma separated names, which aren't helmets
            return helmetName.All(c => char.IsLetter(c) || c == '_') && Enum.TryParse(helmetName, true, out helmet);
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
                armors = armors.Skip(page * ItemsPerPage).Take(ItemsPerPage);
            
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

        // The names of a message share one embed description, so a long list goes out as several messages.
        // Every name is sent, because armor styles stay around and /armor list doesn't show which ones are new.
        public static List<(Embed Embed, int ArmorCount)> NewArmorListEmbeds(List<ArmorStyle> newArmors)
        {
            List<(Embed, int)> embeds = new();

            int maxLength = EmbedBuilder.MaxDescriptionLength - FormatArmorNames("").Length;
            string armorNames = "";
            int armorCount = 0;

            foreach (var armor in newArmors)
            {
                // The title of the armor's own embed can't hold more, and a name that fits no message would hold up the rest
                string armorName = UIUtils.shortenTextForEmbed(armor.ArmorName, EmbedBuilder.MaxTitleLength) + "\n";

                if (armorNames.Length + armorName.Length > maxLength)
                {
                    embeds.Add((NewArmorListEmbed(armorNames), armorCount));
                    armorNames = "";
                    armorCount = 0;
                }

                armorNames += armorName;
                armorCount++;
            }

            if (armorCount > 0)
                embeds.Add((NewArmorListEmbed(armorNames), armorCount));

            return embeds;
        }

        // Discord reads a single word on the first line as the language of the block and hides it, so that line stays empty
        private static string FormatArmorNames(string armorNames)
        {
            return $"```\n{armorNames}```";
        }

        private static Embed NewArmorListEmbed(string armorNames)
        {
            var embed = new EmbedBuilderTemplate()
            .WithTitle($"New Armor Styles:")
            .WithDescription(FormatArmorNames(armorNames))
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
            var armorNames = await ArmorListNamesFormated(helmetType, armors, page);

            var embed = new EmbedBuilderTemplate()
            .WithTitle($"Armor List: {helmetType.ToName()}")
            .WithDescription(armorNames.Length > 0 ? FormatArmorNames(armorNames) : "No armor styles yet.")
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

        // Without an image when there's no screenshot or it can't be fetched, so the lookup still answers
        public async Task<(Embed ArmorEmbed, FileAttachment? ArmorImage)> ArmorEmbed(ArmorStyle armor = null, string armorName = "Free Palestine")
        {
            if (armor == null)
                armor = await GetArmorStyle(armorName);

            var imageName = FormatImageURL(armor.ArmorName, ".png");
            var image = await GetFileAttachmentAsync(FormatImageURL(armor.ArmorScreenshot, includeSpecial: true), imageName, armor.ArmorDescription);

            var embed = new EmbedBuilderTemplate()
                .WithTitle(armor.ArmorName)
                // The empty first line keeps a one-word description from being read as the language of the block
                .WithDescription($"```\n{armor.ArmorDescription}\nMade by {armor.Author}```")
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
                ]);

            if (image != null)
                embed.WithImageUrl($"attachment://{imageName}");

            return (embed.Build(), image);
        }

        // Null when the url isn't a web link or the image can't be fetched in time
        public async Task<FileAttachment?> GetFileAttachmentAsync(string url, string fileName, string description = "")
        {
            // An empty screenshot reaches here as "MinerPreview", and on Linux a path like /armor.png is an absolute file URI
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            try
            {
                // The whole image is read inside the limit, so a server that stops halfway can't hold up the reply
                using var limit = new CancellationTokenSource(ScreenshotLimit);
                var image = await _httpClient.GetByteArrayAsync(uri, limit.Token);

                return new FileAttachment(new MemoryStream(image), fileName, UIUtils.shortenTextForEmbed(description, MaxAttachmentDescriptionLength));
            }
            catch (Exception e)
            {
                // The limit ends the fetch with a cancellation, which doesn't say why
                var reason = e is OperationCanceledException ? $"no answer within {ScreenshotLimit.TotalSeconds:0.#} seconds" : e.Message;

                Console.WriteLine($"Couldn't fetch the screenshot for {fileName}: {reason}");
                return null;
            }
        }

        public async Task<Embed> ErrorEmbed()
        {
            var embed = new EmbedBuilderTemplate()
                .WithTitle("hmmm...")
                .WithDescription("That armor is not in my armor list. Please type /armor list to see the full armor list.")
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
                .WithDescription(FormatArmorNames(string.Concat(Enum.GetValues<HelmentType>().Select(helmet => helmet.ToName() + "\n"))))
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
