using System.Globalization;

namespace MinerBot_2._0.Status
{
    // The file Docker's health check reads. The status sampler rewrites it every 5 seconds while the bot is up on Discord,
    // so a bot that lost Discord, or hung, stops rewriting it and turns unhealthy. It holds the UTC time it was written and
    // the run's id, and nothing secret.
    public static class Heartbeat
    {
        // How old the heartbeat may be, and how far ahead of the clock, for the bot to count as healthy
        public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

        // In the container's /tmp, which the bot and the health check share: both run as the image's user, with its settings
        public static string DefaultPath => Path.Combine(Path.GetTempPath(), "minerbot-heartbeat");

        // A temporary file and then a move, so the check never reads half of one
        public static void Write(string path, DateTimeOffset now, ulong runId)
        {
            var temporary = path + ".tmp";

            File.WriteAllText(temporary, string.Create(CultureInfo.InvariantCulture, $"{now.ToUniversalTime():O} {runId}\n"));
            File.Move(temporary, path, overwrite: true);
        }

        // The start's first step, before anything that could take a while: a container that Docker restarts keeps its /tmp,
        // and the run before's heartbeat would make this one look healthy before it is. It never stops the start.
        public static void DeleteOld(string path)
        {
            try
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
            }
            catch (Exception e)
            {
                Console.WriteLine($"The heartbeat file of the run before couldn't be deleted ({e.GetType().Name})");
            }
        }

        // What dotnet "MinerBot 2.0.dll" healthcheck answers: 0 when the heartbeat is at most a minute from now, otherwise
        // 1, each with one line saying why, which Docker keeps with the check's result
        public static (int ExitCode, string Line) Check(string path, DateTimeOffset now)
        {
            string text;

            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return (1, "Unhealthy: there's no heartbeat file, so the bot hasn't been up on Discord since it started");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (1, $"Unhealthy: the heartbeat file can't be read ({e.GetType().Name})");
            }

            var written = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            if (written == null || !DateTimeOffset.TryParseExact(written, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var beat))
                return (1, "Unhealthy: the heartbeat file holds no time it can read");

            var age = now - beat;

            if (age > Limit)
                return (1, string.Create(CultureInfo.InvariantCulture, $"Unhealthy: the heartbeat is {age.TotalSeconds:0} s old, so the bot isn't up on Discord"));

            if (-age > Limit)
                return (1, string.Create(CultureInfo.InvariantCulture, $"Unhealthy: the heartbeat is {-age.TotalSeconds:0} s ahead of the clock"));

            return (0, string.Create(CultureInfo.InvariantCulture, $"Healthy: the heartbeat is {Math.Max(0, age.TotalSeconds):0} s old"));
        }
    }
}
