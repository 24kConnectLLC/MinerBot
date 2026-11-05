using System.Net;
using System.Security.Claims;
using DataAccessLibrary.Audit;
using DataAccessLibrary.Tests.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MinerBot.Dashboard.Auth;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The role comes from Dashboard:Owners and Dashboard:Admins on every request and is never stored, so a change to the
    // lists applies at once
    [Collection("Database")]
    public class RoleTests(MariaDbFixture fixture)
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        // Fails with IOptions instead of IOptionsMonitor, with a role kept in the ticket, or with sessions left behind
        [Fact]
        public async Task AdminRemovedFromTheListLosesAccessAtTheNextRequest()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var removedHere = factory.Visitor("203.0.113.40");
            var removedThere = factory.Visitor("203.0.113.41");
            var stays = factory.Visitor("203.0.113.42");
            var hereKey = await SignInSteps.SignedInAsync(factory, removedHere, TestUsers.Admin);
            var thereKey = await SignInSteps.SignedInAsync(factory, removedThere, TestUsers.Admin);
            var staysKey = await SignInSteps.SignedInAsync(factory, stays, TestUsers.SecondAdmin);

            factory.Settings.Change(("Dashboard:Admins", TestUsers.SecondAdmin.IdText));

            var response = await removedHere.Client.GetAsync("/admin", Token);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/signin?returnUrl=%2Fadmin", SignInSteps.PathOf(response.Headers.Location));
            Assert.Null(await db.SessionAsync(hereKey));
            Assert.Null(await db.SessionAsync(thereKey));
            Assert.NotNull(await db.SessionAsync(staysKey));

            var refused = Assert.Single(await db.AuditAsync(), row => row.action == "auth.refused");
            Assert.Equal(TestUsers.Admin.Id, refused.actor_id);
            Assert.Equal(AuditRole.Visitor, refused.actor_role);
            Assert.Equal(AuditOutcome.Refused, refused.outcome);

            // The other session of theirs is gone too, without another row, and the admin who stayed still gets in
            Assert.Equal(HttpStatusCode.Redirect, (await removedThere.Client.GetAsync("/admin", Token)).StatusCode);
            Assert.Single(await db.AuditAsync(), row => row.action == "auth.refused");
            Assert.Equal(HttpStatusCode.OK, (await stays.Client.GetAsync("/admin", Token)).StatusCode);
        }

        [Fact]
        public async Task RoleIsWorkedOutOnEveryRequest()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var admin = factory.Visitor();
            await SignInSteps.SignedInAsync(factory, admin, TestUsers.Admin);

            Assert.Contains("with the Admin role", await admin.Client.GetStringAsync("/admin", Token));

            factory.Settings.Change(
                ("Dashboard:Owners", $"{TestUsers.Owner.IdText},{TestUsers.Admin.IdText}"),
                ("Dashboard:Admins", TestUsers.SecondAdmin.IdText));

            Assert.Contains("with the Owner role", await admin.Client.GetStringAsync("/admin", Token));
        }

        [Fact]
        public async Task RoleIsNeverStoredInTheTicket()
        {
            var db = await fixture.ReadyAsync();
            await using var factory = new DashboardFactory(db);
            var key = await SignInSteps.SignedInAsync(factory, factory.Visitor(), TestUsers.Owner);
            var row = (await db.SessionAsync(key))!;

            var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(DbTicketStore.TicketPurpose).CreateProtector(key);
            var ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(row.ticket))!;

            Assert.Equal([ClaimTypes.Name, ClaimTypes.NameIdentifier, DashboardAuth.MfaClaim], ticket.Principal.Claims.Select(claim => claim.Type).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(ticket.Principal.Claims, claim => claim.Type == ClaimTypes.Role);
        }

        // Principals built the way the sign-in builds them, then given their roles the way every request does
        static async Task<ClaimsPrincipal> WithRolesAsync(IServiceProvider services, ulong id, bool mfa)
        {
            var signedIn = new SignedInUser(id, "Test", mfa).ToPrincipal();
            return await services.GetRequiredService<IClaimsTransformation>().TransformAsync(signedIn);
        }

        [Fact]
        public async Task PoliciesFollowTheRoles()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var services = factory.Services;
            var authorization = services.GetRequiredService<IAuthorizationService>();

            async Task<bool> Passes(ClaimsPrincipal user, string policy) => (await authorization.AuthorizeAsync(user, policy)).Succeeded;

            var owner = await WithRolesAsync(services, TestUsers.Owner.Id, true);
            var admin = await WithRolesAsync(services, TestUsers.Admin.Id, true);
            var visitor = await WithRolesAsync(services, TestUsers.Visitor.Id, true);
            var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

            Assert.True(await Passes(owner, Policies.Admin));
            Assert.True(await Passes(admin, Policies.Admin));
            Assert.False(await Passes(visitor, Policies.Admin));
            Assert.False(await Passes(anonymous, Policies.Admin));

            Assert.True(await Passes(owner, Policies.Owner));
            Assert.False(await Passes(admin, Policies.Owner));
            Assert.False(await Passes(visitor, Policies.Owner));
            Assert.False(await Passes(anonymous, Policies.Owner));

            // A role only with two-factor on, whatever the lists say
            Assert.Equal(DashboardRole.None, RoleClaims.RoleOf(await WithRolesAsync(services, TestUsers.Owner.Id, false)));
            Assert.Equal(DashboardRole.Owner, RoleClaims.RoleOf(owner));
            Assert.Equal(DashboardRole.Admin, RoleClaims.RoleOf(admin));
        }

        // It can run more than once a request, so it's idempotent, never changes its input, and leaves anonymous alone
        [Fact]
        public async Task RoleClaimsAreAddedOnceToACopy()
        {
            await using var factory = new DashboardFactory(await fixture.ReadyAsync());
            var transformation = factory.Services.GetRequiredService<IClaimsTransformation>();
            var signedIn = new SignedInUser(TestUsers.Owner.Id, "Test Owner", true).ToPrincipal();

            var once = await transformation.TransformAsync(signedIn);
            var twice = await transformation.TransformAsync(once);

            Assert.DoesNotContain(signedIn.Claims, claim => claim.Type == ClaimTypes.Role);
            Assert.Single(twice.Claims, claim => claim.Type == ClaimTypes.Role);
            Assert.Same(once, twice);

            var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.Same(anonymous, await transformation.TransformAsync(anonymous));
        }
    }
}
