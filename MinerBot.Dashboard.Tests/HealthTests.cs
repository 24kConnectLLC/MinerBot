using System.Diagnostics;
using System.Net;
using DataAccessLibrary.Tests.Database;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // /health/live answers while the process does, and /health/ready only while both databases answer, each within a
    // short limit. The answer is one word, so nothing of a database can show through it.
    [Collection("Database")]
    public class HealthTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static async Task AssertAnswerAsync(HttpResponseMessage response, HttpStatusCode status, string word)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(word, await response.Content.ReadAsStringAsync(Token));
        }

        [Fact]
        public async Task BothAreHealthyWithTheDatabasesUp()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            using var local = factory.Local();

            await AssertAnswerAsync(await local.GetAsync("/health/live", Token), HttpStatusCode.OK, "Healthy");
            await AssertAnswerAsync(await local.GetAsync("/health/ready", Token), HttpStatusCode.OK, "Healthy");
        }

        // Fails if a database isn't checked, or if the answer carries anything but the word. Nothing listens on a dead
        // database's port, so this answer comes quickly without any limit; the next test is for those.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ReadyIsUnhealthyWhenEitherDatabaseIsDown(bool botDown)
        {
            var db = await fixture.ReadyAsync();
            var dead = new DeadDatabase();
            await using var factory = new DashboardFactory(db, botDown
                ? new DashboardFactoryOptions { BotConnection = dead.BotConnection }
                : new DashboardFactoryOptions { SiteConnection = dead.SiteConnection });
            using var local = factory.Local();
            var watch = Stopwatch.StartNew();

            await AssertAnswerAsync(await local.GetAsync("/health/ready", Token), HttpStatusCode.ServiceUnavailable, "Unhealthy");

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The answer took {watch.Elapsed}");

            // The process still answers, so the container isn't restarted over a database
            await AssertAnswerAsync(await local.GetAsync("/health/live", Token), HttpStatusCode.OK, "Healthy");
        }

        // A database that takes the connection and never answers, as a hung MariaDB would. Its connection string would
        // wait a minute, so only the dashboard's own limits end the wait. Fails if both of them are lost.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ReadyGivesUpOnADatabaseThatNeverAnswers(bool botSilent)
        {
            var db = await fixture.ReadyAsync();
            using var silent = new SilentDatabase();
            await using var factory = new DashboardFactory(db, botSilent
                ? new DashboardFactoryOptions { BotConnection = silent.Connection }
                : new DashboardFactoryOptions { SiteConnection = silent.Connection });
            using var local = factory.Local();

            // The app is up before the watch starts, so only the check is timed
            await AssertAnswerAsync(await local.GetAsync("/health/live", Token), HttpStatusCode.OK, "Healthy");
            var watch = Stopwatch.StartNew();

            await AssertAnswerAsync(await local.GetAsync("/health/ready", Token), HttpStatusCode.ServiceUnavailable, "Unhealthy");

            // At least a second, which shows the server really held the probe rather than refusing it
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
        }

        // Reads only, so another method gets 405 from routing, after the origin check has turned it away
        [Fact]
        public async Task HealthChecksTakeOnlyReads()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            using var local = factory.Local();

            var head = await local.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health/live"), Token);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);

            var post = await local.PostAsync("/health/live", new StringContent(""), Token);
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }
    }
}
