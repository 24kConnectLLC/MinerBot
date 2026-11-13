using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The test hosts poll for file changes, so none leaves a watcher running after it's disposed (Support/FilePolling.cs)
    [Collection("Database")]
    public class FileWatchingTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task TestHostsPollForFileChanges()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);

            // A page with the layout, whose versioned stylesheet link has the web root watch the file
            Assert.Equal(HttpStatusCode.OK, (await factory.Visitor().Client.GetAsync("/", Token)).StatusCode);

            // A provider that polls hands out a token that polls, and has no watcher. One that watches hands out a plain
            // cancellation token and starts a watcher.
            var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
            var watched = new[]
            {
                (Files: environment.WebRootFileProvider, File: "css/site.css"),
                (Files: environment.ContentRootFileProvider, File: "appsettings.json")
            };

            foreach (var (files, file) in watched)
            {
                var token = Assert.IsType<CompositeChangeToken>(files.Watch(file));
                Assert.Contains(token.ChangeTokens, inner => inner is PollingFileChangeToken { ActiveChangeCallbacks: true });
            }
        }
    }
}
