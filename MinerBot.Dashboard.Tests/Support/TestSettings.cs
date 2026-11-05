using Microsoft.Extensions.Configuration;

namespace MinerBot.Dashboard.Tests.Support
{
    // The factory's settings as a source a test can change while the app runs. It comes last, so it wins, and a change
    // reloads it the way the options would see a changed configuration.
    internal sealed class TestSettings : ConfigurationProvider, IConfigurationSource
    {
        public TestSettings(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
                Data[key] = value;
        }

        public IReadOnlyDictionary<string, string?> Values => new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

        // Several keys at once, so the lists never pass through a state that fails validation
        public void Change(params (string Key, string? Value)[] changes)
        {
            foreach (var (key, value) in changes)
                Data[key] = value;

            OnReload();
        }
    }
}
