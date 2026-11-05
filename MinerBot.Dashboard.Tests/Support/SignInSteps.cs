using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using MinerBot.Dashboard.Auth;

namespace MinerBot.Dashboard.Tests.Support
{
    // What Discord's authorize page would have been sent: the state, which only the app's keys can make, and the PKCE
    // challenge the fake Discord ties its code to
    internal sealed record Authorize(Uri Location, IReadOnlyDictionary<string, string> Query)
    {
        public string State => Query["state"];
        public string CodeChallenge => Query["code_challenge"];
        public string RedirectUri => Query["redirect_uri"];
    }

    // A browser's steps through the sign-in, with the same cookie jar throughout, as the correlation cookie needs
    internal static partial class SignInSteps
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        public static async Task<Authorize> StartAsync(TestBrowser browser, string? returnUrl = ReturnUrls.Default, string authorizeEndpoint = FakeDiscord.DiscordAuthorizeEndpoint)
        {
            var response = await browser.Client.GetAsync(returnUrl == null ? "/signin" : "/signin?returnUrl=" + Uri.EscapeDataString(returnUrl), Token);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var location = response.Headers.Location!;
            Assert.Equal(authorizeEndpoint, location.GetLeftPart(UriPartial.Path));

            return new Authorize(location, QueryHelpers.ParseQuery(location.Query).ToDictionary(field => field.Key, field => field.Value.ToString()));
        }

        public static Task<HttpResponseMessage> CallbackAsync(TestBrowser browser, string code, string state) =>
            browser.Client.GetAsync($"{DashboardAuth.CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", Token);

        // The whole sign-in, ending with the callback's answer
        public static async Task<HttpResponseMessage> SignInAsync(DashboardFactory factory, TestBrowser browser, TestUser user, string? returnUrl = ReturnUrls.Default)
        {
            var authorize = await StartAsync(browser, returnUrl, factory.Discord.AuthorizeEndpoint);
            var code = factory.Discord.Expect(authorize.CodeChallenge, user);

            return await CallbackAsync(browser, code, authorize.State);
        }

        // A sign-in that has to work, giving the session's key
        public static async Task<string> SignedInAsync(DashboardFactory factory, TestBrowser browser, TestUser user)
        {
            var response = await SignInAsync(factory, browser, user);

            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"The sign-in answered {response.StatusCode}; the fake Discord refused: {string.Join(", ", factory.Discord.Refusals)}");
            Assert.Equal(ReturnUrls.Default, PathOf(response.Headers.Location));

            return browser.SessionKey ?? throw new InvalidOperationException("The sign-in set no session cookie");
        }

        // The antiforgery token from a page's form
        public static async Task<string> FormTokenAsync(TestBrowser browser, string page = ReturnUrls.Default)
        {
            var html = await browser.Client.GetStringAsync(page, Token);
            return FormToken(html);
        }

        public static string FormToken(string html)
        {
            var match = FormTokenField().Match(html);
            Assert.True(match.Success, "The page has no antiforgery field");
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }

        // A sign-out from the dashboard's own page, as a browser sends it
        public static async Task<HttpResponseMessage> SignOutAsync(TestBrowser browser)
        {
            var token = await FormTokenAsync(browser);
            return await PostAsync(browser, "/signout", token, origin: DashboardFactory.PublicOrigin);
        }

        public static Task<HttpResponseMessage> PostAsync(TestBrowser browser, string path, string? token, string? origin = null, string? fetchSite = null, HttpMethod? method = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(token == null ? [] : [new KeyValuePair<string, string>("__RequestVerificationToken", token)])
            };

            if (origin != null)
                request.Headers.Add("Origin", origin);

            if (fetchSite != null)
                request.Headers.Add("Sec-Fetch-Site", fetchSite);

            return browser.Client.SendAsync(request, Token);
        }

        // A redirect's address without the host, so a relative and an absolute Location compare the same
        public static string? PathOf(Uri? location) => location == null ? null : location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;

        [GeneratedRegex("""<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"[^>]*>""")]
        private static partial Regex FormTokenField();
    }
}
