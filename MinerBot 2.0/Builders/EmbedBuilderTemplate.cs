using Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Builders
{
    public class EmbedBuilderTemplate : EmbedBuilder
    {
        public EmbedBuilderTemplate()
        {
            var author = new EmbedAuthorBuilder()
                .WithName("MinerBot by 24kConnect" + " - Version 2.0")
                .WithIconUrl("https://24kconnect.com/wp-content/uploads/24kConnectLogoRemadeBlackx512.png")
                .WithUrl("https://24kconnect.com/");

            var footer = new EmbedFooterBuilder()
                .WithIconUrl("https://24kconnect.com/wp-content/uploads/24kConnectLogoRemadeBlackx512.png")
                .WithText("MinerBot by 24kConnect" + " - Version 2.0");

            this.WithFooter(footer)
                //.WithAuthor(author)
                .WithColor(Discord.Color.DarkBlue);
        }
    }
}
