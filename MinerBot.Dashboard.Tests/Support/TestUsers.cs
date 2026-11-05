using System.Globalization;

namespace MinerBot.Dashboard.Tests.Support
{
    // A Discord account as the fake Discord describes it. MfaEnabled null leaves mfa_enabled out of the answer.
    internal sealed record TestUser(ulong Id, string UserName, string? GlobalName, bool? MfaEnabled = true)
    {
        public string DisplayName => GlobalName ?? UserName;

        public string IdText => Id.ToString(CultureInfo.InvariantCulture);
    }

    // Made up, in the same style as DataAccessLibrary.Tests' IDs. The factory lists the Owner and both Admins.
    internal static class TestUsers
    {
        public static readonly TestUser Owner = new(1000000000000000001, "test_owner", "Test Owner");
        public static readonly TestUser Admin = new(1000000000000000002, "test_admin", "Test Admin");
        public static readonly TestUser SecondAdmin = new(1000000000000000003, "second_admin", "Second Admin");
        public static readonly TestUser Visitor = new(1000000000000000099, "test_visitor", "Test Visitor");
    }
}
