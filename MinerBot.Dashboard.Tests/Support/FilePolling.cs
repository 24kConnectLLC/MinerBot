using System.Runtime.CompilerServices;

namespace MinerBot.Dashboard.Tests.Support
{
    // Every file provider in a test run polls for changes instead of watching. A host's web root starts a watcher for the
    // layout's versioned stylesheet link, and the watcher keeps running after the host is disposed. On Linux each watcher
    // holds an inotify instance, and GitHub's runner allows 128, so a full run ran out of them: hosts failed to start and
    // pages answered 500. FileWatchingTests checks this is in place.
    internal static class FilePolling
    {
        [ModuleInitializer]
        internal static void Start() => Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
    }
}
