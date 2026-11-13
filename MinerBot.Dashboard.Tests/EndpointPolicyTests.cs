using System.Net;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // Deny by default, enforced here instead of a fallback policy: every endpoint has a policy, or is on the public list
    // kept in this test
    [Collection("Database")]
    public class EndpointPolicyTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Everything anyone may open. A new public page has to be added here on purpose.
        static readonly HashSet<string> Public = ["", "Index", "SignIn", "SignOut", "Denied", "Error", "health/live", "health/ready"];

        static string Route(RouteEndpoint endpoint) => endpoint.RoutePattern.RawText!.Trim('/');

        // Fails when a page is added without a policy, a public page loses AllowAnonymous, the folder convention goes, or
        // a MapPost appears, which the antiforgery filter wouldn't cover
        [Fact]
        public async Task EveryEndpointHasAPolicyOrIsListedAsPublic()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
            var routes = endpoints.Select(Route).ToHashSet();

            Assert.Superset(Public, routes);
            Assert.Contains("Admin", routes);

            // The armor pages, spelt with the capital A that the check below needs
            Assert.Contains("Admin/Armor", routes);
            Assert.Contains("Admin/Armor/{id:long}", routes);

            foreach (var endpoint in endpoints)
            {
                var route = Route(endpoint);
                var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null;
                var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).ToList();

                if (Public.Contains(route))
                {
                    Assert.True(anonymous, $"{route} is public but doesn't allow anonymous visitors");
                    Assert.Empty(policies);
                }
                else
                {
                    Assert.False(anonymous, $"{route} isn't on the public list but allows anonymous visitors");
                    Assert.NotEmpty(policies);
                    Assert.All(policies, policy => Assert.Contains(policy, new[] { Policies.Admin, Policies.Owner }));

                    if (route.StartsWith("Admin", StringComparison.Ordinal))
                        Assert.Contains(Policies.Admin, policies);
                }

                // A method other than GET or HEAD only on a Razor page, whose antiforgery check covers it
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                var readsOnly = methods != null && methods.All(method => method is "GET" or "HEAD");

                Assert.True(readsOnly || endpoint.Metadata.GetMetadata<PageActionDescriptor>() != null, $"{route} takes other methods but isn't a Razor page");

                // Only the error page, which writes nothing, and sign-out skip Razor Pages' antiforgery check. Sign-out checks
                // the token itself whenever there's a session to end, which ForgeryTests shows.
                if (endpoint.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>() != null)
                    Assert.Contains(route, new[] { "Error", "SignOut" });
            }
        }

        [Fact]
        public async Task AnonymousAdminGoesToSignIn()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var response = await factory.Visitor().Client.GetAsync("/admin", Token);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf(response.Headers.Location));
        }

        // Signed in without a role is impossible, because the store refuses it, but the policy would stop it anyway
        [Fact]
        public async Task EveryAdminPageAsksForTheAdminPolicy()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());

            var admin = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(endpoint => Route(endpoint).StartsWith("Admin", StringComparison.Ordinal)).ToList();

            Assert.NotEmpty(admin);
            Assert.All(admin, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == Policies.Admin));
        }
    }
}
