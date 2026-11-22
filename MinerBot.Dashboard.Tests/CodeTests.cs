using System.Text.RegularExpressions;

namespace MinerBot.Dashboard.Tests
{
    // Rules the dashboard's code keeps everywhere, read from the code itself, which the test's build copies beside it: the
    // dashboard's, and DataAccessLibrary's, where its SQL is
    public partial class CodeTests
    {
        // The statement that lists a login's grants prints its password's hash, so nothing the dashboard runs may ask for it,
        // not even in a comment that could be copied into code: the Sessions page reads the login's rights from
        // information_schema instead. Fails for that statement anywhere in either's code, in any case and any spacing.
        [Fact]
        public void NothingAsksForShowGrants()
        {
            foreach (var folder in new[] { "dashboard-source", "data-source" })
            {
                var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, folder), "*.cs", SearchOption.AllDirectories);

                Assert.True(files.Length > 20, $"Only {files.Length} files were copied into {folder}");

                foreach (var file in files)
                    Assert.False(ShowGrants().IsMatch(File.ReadAllText(file)), $"{Path.GetRelativePath(AppContext.BaseDirectory, file)} asks for SHOW GRANTS");
            }

            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "data-source", "Dashboard", "DashboardChecks.cs")), "The checks' reads weren't copied");
            Assert.Matches(ShowGrants(), "show\n  Grants");
        }

        [GeneratedRegex(@"\bSHOW\s+GRANTS\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex ShowGrants();
    }
}
