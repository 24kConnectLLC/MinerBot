using Discord;
using Discord.Interactions;
using MinerBot_2._0.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MinerBot_2._0.Handlers
{
    public class ArmorAutocompleteHandler : AutocompleteHandler
    {
        public ArmorService _ArmorService { get; set; }

        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
        {
            // Create a collection with suggestions for autocomplete
            List<AutocompleteResult> results = new();

            string currentValue = autocompleteInteraction.Data.Current.Value.ToString();

            foreach (var armor in await _ArmorService.GetArmorListNames())
            {
                // max - 25 suggestions at a time (API Limit)
                if (results.Count >= 25)
                    break;

                if (armor.Contains(currentValue,StringComparison.OrdinalIgnoreCase))
                    results.Add(new AutocompleteResult(armor, armor));
            }

            return AutocompletionResult.FromSuccess(results); // .Take(25)
        }
    }
}
