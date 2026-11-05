using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DataAccessLibrary;
using DataAccessLibrary.Dashboard;
using DataAccessLibrary.Dashboard.Models;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Security;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // Error pages say what happened and give a request ID, and never show anything of the exception: no type, no stack,
    // nothing of the database's address, names or login
    [Collection("Database")]
    public partial class ErrorPageTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static void AssertNoDetails(string page, params string[] alsoNever)
        {
            foreach (var detail in new[] { "Exception", "MySql", "   at ", "Server=", "StackTrace", "System." }.Concat(alsoNever))
                Assert.DoesNotContain(detail, page);
        }

        static string RequestId(string page)
        {
            var match = RequestIdText().Match(page);
            Assert.True(match.Success, "The page has no request ID");
            return match.Groups[1].Value;
        }

        [Fact]
        public async Task DatabaseDownGivesA503PageWithoutDetails()
        {
            var db = await fixture.ReadyAsync();
            var dead = new DeadDatabase();
            await using var factory = new DashboardFactory(db, new DashboardFactoryOptions { SiteConnection = dead.SiteConnection, BotConnection = dead.BotConnection });
            var browser = factory.Visitor();
            browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });

            var response = await browser.Client.GetAsync("/admin", Token);
            var page = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("can't reach its database just now", WebUtility.HtmlDecode(page));
            AssertNoDetails(page, dead.Port.ToString(), dead.Login, dead.Password, dead.SiteDatabase, dead.BotDatabase);

            // The ID on the page is the one in the log, so it can be found there
            var id = RequestId(page);
            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(id, StringComparison.Ordinal));
            Assert.DoesNotContain(dead.Password, factory.Logs.Text);
            Assert.DoesNotContain(dead.Login, factory.Logs.Text);
        }

        // Fails if the exception handler's own logging isn't suppressed, which logs the exception's message
        [Fact]
        public async Task UnexpectedFailureGivesA500PageAndLogsNoMessage()
        {
            var marker = "marker-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            await using var factory = new DashboardFactory(await fixture.ReadyAsync(), new DashboardFactoryOptions
            {
                Services = services => services.Replace(ServiceDescriptor.Singleton<IDashboardSessionData>(new BrokenSessions(marker)))
            });
            var browser = factory.Visitor();
            browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });

            var response = await browser.Client.GetAsync("/admin", Token);
            var page = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("Something went wrong, and nothing was changed.", WebUtility.HtmlDecode(page));
            Assert.DoesNotContain(marker, page);
            AssertNoDetails(page);
            RequestId(page);

            Assert.Contains(factory.Logs.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
            Assert.DoesNotContain(marker, factory.Logs.Text);
        }

        sealed class BrokenSessions(string marker) : IDashboardSessionData
        {
            public Task<DashboardSession?> GetSession(string key, DateTime now) => throw new InvalidOperationException(marker);

            public Task CreateSession(string key, DashboardSession session, DataTransaction? transaction = null) => throw new InvalidOperationException(marker);
            public Task<int> DeleteExpiredSessions(DateTime expiredBefore, DateTime unusedBefore) => throw new InvalidOperationException(marker);
            public Task<int> DeleteOldestSessions(ulong userId, int keep, DataTransaction? transaction = null) => throw new InvalidOperationException(marker);
            public Task<int> DeleteOtherSessions(ulong userId, string keepKey, DataTransaction? transaction = null) => throw new InvalidOperationException(marker);
            public Task<bool> DeleteSession(string key, DataTransaction? transaction = null) => throw new InvalidOperationException(marker);
            public Task<bool> RenewSession(string key, byte[] ticket) => throw new InvalidOperationException(marker);
            public Task<T> RunInTransaction<T>(Func<DataTransaction, Task<T>> work) => throw new InvalidOperationException(marker);
            public Task<bool> TouchSession(string key, DateTime lastSeenAt) => throw new InvalidOperationException(marker);
        }

        [Fact]
        public async Task UnknownPageGivesA404PageWithARequestId()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await factory.Visitor().Client.GetAsync("/nothing-here", Token);
            var page = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("There's no page at that address.", WebUtility.HtmlDecode(page));
            Assert.NotEmpty(RequestId(page));
            AssertNoDetails(page);
        }

        // Fails if static files move after UseAuthentication, when the stylesheet would need the database
        [Fact]
        public async Task StylesheetLoadsWhileTheDatabaseIsDown()
        {
            var dead = new DeadDatabase();
            await using var factory = new DashboardFactory(await fixture.ReadyAsync(), new DashboardFactoryOptions { SiteConnection = dead.SiteConnection, BotConnection = dead.BotConnection });
            var browser = factory.Visitor();
            browser.Jar.Add(DashboardFactory.PublicAddress, new Cookie(DashboardAuth.SessionCookie, SessionKeys.New()) { Secure = true });

            var response = await browser.Client.GetAsync("/css/site.css", Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        }

        // A body over Kestrel's limit is the visitor's doing, so it keeps its own status with a plain page, not a 500.
        // TestServer has no body limit, so the handler's page is checked on its own.
        [Theory]
        [InlineData(nameof(BadHttpRequestException), StatusCodes.Status413PayloadTooLarge, "That request was too big")]
        [InlineData(nameof(DashboardUnavailableException), StatusCodes.Status503ServiceUnavailable, "can't reach its database")]
        [InlineData(nameof(InvalidOperationException), StatusCodes.Status500InternalServerError, "Something went wrong")]
        public async Task EachFailureGetsItsOwnStatusAndAPlainPage(string failure, int status, string words)
        {
            var marker = "marker-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            Exception exception = failure switch
            {
                nameof(BadHttpRequestException) => new BadHttpRequestException(marker, StatusCodes.Status413PayloadTooLarge),
                nameof(DashboardUnavailableException) => new DashboardUnavailableException(),
                _ => new InvalidOperationException(marker)
            };

            var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
            context.Response.Body = new MemoryStream();
            context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = exception, Path = "/signout" });

            await ErrorResponses.WriteAsync(context);

            var page = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
            Assert.Equal(status, context.Response.StatusCode);
            Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
            Assert.Contains(words, WebUtility.HtmlDecode(page));
            Assert.DoesNotContain(marker, page);
            Assert.Contains("<html lang=\"en\">", page);
            Assert.Contains("/css/site.css", page);
            AssertNoDetails(page);
            RequestId(page);
        }

        [GeneratedRegex("Request ID: <code>([^<]+)</code>")]
        private static partial Regex RequestIdText();
    }
}
