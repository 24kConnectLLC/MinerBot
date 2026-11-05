using DataAccessLibrary.Tests.Database;
using Xunit.Sdk;

namespace DataAccessLibrary.Tests
{
    // What the database tests do when MariaDB couldn't start, checked without stopping Docker. A missing Docker skips
    // them with the reason, except where MINERBOT_DB_TESTS is required, as in CI, and anything else fails them.
    public class DatabaseRequirementTests
    {
        const string DockerMissing = "Docker is either not running or misconfigured.";

        [Fact]
        public void DatabaseThatStartedIsUsed()
        {
            MariaDbFixture.Require(null, null, "required");
            MariaDbFixture.Require(null, null, null);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("optional")]
        public void MissingDockerSkipsWithTheReason(string? setting)
        {
            // Caught by hand, because Assert.Throws lets a skip through to skip this test too
            var reason = "";

            try
            {
                MariaDbFixture.Require(DockerMissing, null, setting);
            }
            catch (SkipException skip)
            {
                reason = skip.Message;
            }

            Assert.EndsWith("Docker isn't available, so the database tests are skipped: Docker is either not running or misconfigured. Set MINERBOT_DB_TESTS=required to make this a failure.", reason);
        }

        [Theory]
        [InlineData("required")]
        [InlineData("REQUIRED")]
        public void MissingDockerFailsWhenTheTestsAreRequired(string setting)
        {
            var failure = Assert.Throws<InvalidOperationException>(() => MariaDbFixture.Require(DockerMissing, null, setting));

            Assert.Equal($"MINERBOT_DB_TESTS is required, but the database couldn't start: {DockerMissing}", failure.Message);
        }

        // Docker that runs but can't start the container, such as one set to Windows containers, is a failure either way
        [Theory]
        [InlineData(null)]
        [InlineData("required")]
        public void OtherStartFailuresFail(string? setting)
        {
            var cause = new TimeoutException("The container didn't start");

            var failure = Assert.Throws<InvalidOperationException>(() => MariaDbFixture.Require(null, cause, setting));

            Assert.Same(cause, failure.InnerException);
        }
    }
}
