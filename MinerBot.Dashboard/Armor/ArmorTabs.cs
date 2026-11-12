using System.Globalization;
using Microsoft.AspNetCore.Http.Extensions;
using MinerBot.Shared.Armor;

namespace MinerBot.Dashboard.Armor
{
    // A tab of the armor list: the word its link carries, and the website's status it lists, or null for every row
    public sealed record ArmorTab(string Word, string Label, string? Status);

    internal static class ArmorTabs
    {
        public static readonly IReadOnlyList<ArmorTab> All =
        [
            new("pending", "Pending", ArmorStatus.Pending),
            new("approved", "Approved", ArmorStatus.Approved),
            new("not-approved", "Not Approved", ArmorStatus.NotApproved),
            new("all", "All", null)
        ];

        // Pending first, because that's the work
        public static ArmorTab Default => All[0];

        // No word is the default tab. Null for a word that isn't a tab's, so the data layer never gets a status it refuses.
        public static ArmorTab? FromWord(string? word) =>
            string.IsNullOrEmpty(word) ? Default : All.FirstOrDefault(tab => string.Equals(tab.Word, word, StringComparison.OrdinalIgnoreCase));

        // The tab that lists one of the website's words, or All, the only one that lists armor with no status or another
        public static ArmorTab Of(string? status) => All.FirstOrDefault(tab => tab.Status != null && tab.Status == status) ?? All[^1];

        // The list at a tab, with a search when there is one
        public static string Link(ArmorTab tab, string? search)
        {
            var query = new QueryBuilder { { "status", tab.Word } };

            if (!string.IsNullOrEmpty(search))
                query.Add("q", search);

            return "/admin/armor" + query.ToQueryString();
        }

        // An armor's review page, carrying the tab and the search it was opened from, so its Back to the list returns there
        // even once an action has moved the armor to another tab
        public static string ReviewLink(long id, ArmorTab? from, string? search)
        {
            var query = new QueryBuilder();

            if (from != null)
            {
                query.Add("list", from.Word);

                if (!string.IsNullOrEmpty(search))
                    query.Add("q", search);
            }

            return "/admin/armor/" + id.ToString(CultureInfo.InvariantCulture) + query.ToQueryString();
        }
    }
}
