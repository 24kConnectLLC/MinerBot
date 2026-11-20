using DataAccessLibrary.Audit;
using DataAccessLibrary.Status;
using MinerBot.Shared.Requests;

namespace DataAccessLibrary.Tests
{
    // The request list's words, which MinerBot.Shared keeps because it can't reference this library, against the words
    // of the tables they go into. No database is needed.
    public class RequestWordsTests
    {
        // A request's role is the audit row's role, and the dashboard writes both from one word
        [Fact]
        public void RequestRolesAreAuditRoles()
        {
            Assert.Equal(AuditRole.Owner, RequestRoles.Owner);
            Assert.Equal(AuditRole.Admin, RequestRoles.Admin);
            Assert.Equal(AuditRole.ServerAdmin, RequestRoles.ServerAdmin);
            Assert.All(RequestRoles.All, role => Assert.True(AuditRole.IsKnown(role), $"{role} isn't an audit role"));
        }

        // Each kind is the audit row's action, and a kind the bot records in its events once the signature has passed
        [Fact]
        public void KindsFitTheAuditActionAndTheEventKind()
        {
            Assert.All(RequestKinds.All, kind =>
            {
                Assert.Matches(@"^[a-z_]+(\.[a-z_]+)+\z", kind.Word);
                Assert.InRange(kind.Word.Length, 1, 50);
                Assert.Matches(BotStatusData.Kind, kind.Word);
                Assert.InRange(kind.Word.Length, 1, BotStatusData.KindLimit);
            });
        }
    }
}
