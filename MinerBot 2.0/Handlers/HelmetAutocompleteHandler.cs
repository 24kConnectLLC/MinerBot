using Discord;
using Discord.Interactions;
using MinerBot_2._0.Extensions;

namespace MinerBot_2._0.Handlers
{
    public class HelmetAutocompleteHandler : AutocompleteHandler
    {
        public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
        {
            // Create a collection with suggestions for autocomplete
            List<AutocompleteResult> results = new();

            string currentValue = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

            foreach (var helmet in Enum.GetValues<HelmentType>())
            {
                // max - 25 suggestions at a time (API Limit). There are more helmets than that, so the last ones need a letter or two first.
                if (results.Count >= 25)
                    break;

                if (helmet.ToName().Contains(currentValue, StringComparison.OrdinalIgnoreCase))
                    results.Add(new AutocompleteResult(helmet.ToName(), helmet.ToName()));
            }

            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
    }
}
