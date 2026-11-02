using System.Text;
using SteamKit2;

namespace SteamWebAPIAccess.Tests
{
    // Runs the real OnConnected through the real callback manager, without a connection to Steam.
    // All tokens here are made up, and the token file is a temporary one.
    public class SteamSignInTests : IDisposable
    {
        const string SignInFailed = "Unable to sign in to Steam, staying offline";

        readonly string authFolder = Directory.CreateTempSubdirectory("MinerBotSignInTests").FullName;

        public SteamSignInTests()
        {
            // SteamAPIAccess reads these two without checking them
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");
            Environment.SetEnvironmentVariable("STEAM_QRCODE_AUTH", "true");
        }

        public void Dispose() => Directory.Delete(authFolder, recursive: true);

        [Theory]
        [InlineData("")]
        [InlineData("$RT$\n$AT$\n$SID$")]
        [InlineData("$RT$not a token\n$AT$not a token\n$SID$0")]
        [InlineData("$RT$a.b.c\n$AT$a.b.c\n$SID$0")]
        [InlineData("$RT$a.bm90IGpzb24.c\n$AT$a.bm90IGpzb24.c\n$SID$0")]
        public void DamagedTokenFileLeavesSteamOffline(string tokenFile)
        {
            var steam = CreateSteam("user", tokenFile);

            var result = RunOnConnected(steam);

            Assert.Empty(result.Crashes);
            Assert.Contains(SignInFailed, result.Console);
            Assert.False(steam.isRunning);
        }

        [Fact]
        public void MissingUsernameLeavesSteamOffline()
        {
            var steam = CreateSteam(null, ValidTokenFile());

            var result = RunOnConnected(steam);

            Assert.Empty(result.Crashes);
            Assert.Contains(SignInFailed, result.Console);
            Assert.False(steam.isRunning);
        }

        [Fact]
        public void FailedQRSignInLeavesSteamOffline()
        {
            var steam = CreateSteam("user", tokenFile: null);

            var result = RunOnConnected(steam);

            Assert.Empty(result.Crashes);
            Assert.Contains(SignInFailed, result.Console);
            Assert.False(steam.isRunning);
        }

        [Fact]
        public void FailedPasswordSignInLeavesSteamOffline()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;

            var result = RunOnConnected(steam);

            Assert.Empty(result.Crashes);
            Assert.Contains(SignInFailed, result.Console);
            Assert.False(steam.isRunning);
        }

        [Fact]
        public void ValidTokenFileStillSignsIn()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            var result = RunOnConnected(steam);

            Assert.Empty(result.Crashes);
            Assert.DoesNotContain(SignInFailed, result.Console);
            Assert.True(steam.isRunning);
            Assert.False(steam.needsToCreateRefreshToken);
        }

        SteamAPIAccess CreateSteam(string? username, string? tokenFile)
        {
            var steam = new SteamAPIAccess { AuthPath = authFolder, AuthName = "TestToken.txt", isRunning = true };

            steam.SetCredentials("key", "false", username!, null!);
            steam.CreateClient();

            if (tokenFile != null)
                File.WriteAllText(steam.AuthFullPath, tokenFile);

            return steam;
        }

        static string ValidTokenFile()
        {
            var token = CreateToken(DateTimeOffset.UtcNow.AddDays(1));
            return $"$RT${token}\n$AT${token}\n$SID$0";
        }

        static string CreateToken(DateTimeOffset expires)
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"exp":{{expires.ToUnixTimeSeconds()}}}"""));
            return $"header.{payload.TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature";
        }

        // async void hands its exceptions to the synchronization context, and without one they end the process.
        // Catching them here means that losing the try/catch fails these tests instead of killing the test run.
        static (List<Exception> Crashes, string Console) RunOnConnected(SteamAPIAccess steam)
        {
            var realConsole = Console.Out;
            var console = new StringWriter();
            var realContext = SynchronizationContext.Current;
            var context = new CrashCatchingContext();

            Console.SetOut(console);
            SynchronizationContext.SetSynchronizationContext(context);

            try
            {
                var connected = (SteamClient.ConnectedCallback)Activator.CreateInstance(typeof(SteamClient.ConnectedCallback), nonPublic: true)!;

                steam.steamClient.PostCallback(connected);
                steam.UpdateHandlerCallback();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(realContext);
                Console.SetOut(realConsole);
            }

            return (context.Crashes, console.ToString());
        }

        sealed class CrashCatchingContext : SynchronizationContext
        {
            public List<Exception> Crashes { get; } = new();

            public override void Post(SendOrPostCallback callback, object? state)
            {
                try
                {
                    callback(state);
                }
                catch (Exception e)
                {
                    Crashes.Add(e);
                }
            }
        }
    }
}
