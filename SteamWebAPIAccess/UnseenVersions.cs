namespace SteamWebAPIAccess
{
    // Lobbies on a version that isn't in GameVersions show Modes Unknown until someone adds it. Hosts write the version, so
    // a made-up one costs one warning at most, only so many are remembered, and the owner gets one alert per run.
    internal sealed class UnseenVersions(ISteamAPIAccess steamAPI)
    {
        internal const int MaxWarnings = 20;

        readonly HashSet<uint> warned = new();
        bool alerted;

        // Other tests swap the console while the lobby tests run, so those read the warnings here
        internal Action<string> Log { get; set; } = Console.WriteLine;

        // For the tests, since the warnings stop at the limit whether or not more versions are kept
        internal int Remembered
        {
            get
            {
                lock (warned)
                    return warned.Count;
            }
        }

        public void Report(uint version)
        {
            if (GameVersions.IsKnown(version))
                return;

            string? alert = null;

            // The slash command and the notification job can read the lobby list at the same time
            lock (warned)
            {
                if (warned.Count == MaxWarnings || !warned.Add(version))
                    return;

                Log($"Lobby version {version} isn't in the version table, so its lobbies show Modes Unknown under {GameVersions.OtherVersionsHeading}");

                if (warned.Count == MaxWarnings)
                    Log($"That's {MaxWarnings} lobby versions that aren't in the version table, so no more are logged until the bot restarts");

                if (!alerted)
                {
                    alerted = true;
                    alert = $"MinerBot found lobbies on game version {version}, which isn't in its version table. Lobbies on versions it doesn't know show Modes Unknown until the table in SteamWebAPIAccess/GameVersions.cs is updated.";
                }
            }

            // Not awaited, so a slow Discord can't hold up the lobby list
            if (alert != null)
                _ = SendAlert(alert);
        }

        async Task SendAlert(string message)
        {
            try
            {
                if (steamAPI.AlertOwner != null)
                    await steamAPI.AlertOwner(message);
            }
            catch (Exception e)
            {
                Log($"Unable to alert the owner: {e.Message}");
            }
        }
    }
}
