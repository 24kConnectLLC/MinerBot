using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using SteamKit2;
using SteamKit2.Authentication;

namespace SteamWebAPIAccess.Tests
{
    // Runs the real handlers through the real callback manager, without a connection to Steam.
    // All tokens here are made up, and the token file is a temporary one.
    public class SteamSignInTests : IDisposable
    {
        const string SignInFailed = "Unable to sign in to Steam";
        const string BackAgain = "MinerBot is signed in to Steam again.";

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);

        static readonly string GoodToken = CreateToken(DateTimeOffset.UtcNow.AddDays(100));
        static readonly string NewToken = CreateToken(DateTimeOffset.UtcNow.AddDays(200));

        readonly string authFolder = Directory.CreateTempSubdirectory("MinerBotSignInTests").FullName;
        readonly TextWriter realConsole = Console.Out;
        readonly ConsoleCapture console = new();

        string ConsoleText => console.ToString();

        public SteamSignInTests()
        {
            // SteamAPIAccess reads these two without checking them
            Environment.SetEnvironmentVariable("STEAMAPP_ID", "1");
            Environment.SetEnvironmentVariable("STEAM_QRCODE_AUTH", "true");

            Console.SetOut(console);
        }

        public void Dispose()
        {
            Console.SetOut(realConsole);
            Directory.Delete(authFolder, recursive: true);
        }

        // Reading the token file

        [Fact]
        public void ValidTokenFileSignsIn()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);

            var logOn = Assert.Single(steam.LogOns);

            Assert.Equal("user", logOn.Username);
            Assert.Equal(GoodToken, logOn.AccessToken);
            Assert.DoesNotContain(SignInFailed, ConsoleText);
            Assert.Equal(0, steam.Disconnects);
            Assert.False(steam.needsToCreateRefreshToken);
            Assert.Empty(steam.Alerts);
        }

        [Fact]
        public void TokenFileWithWindowsLineEndingsSignsIn()
        {
            var steam = CreateSteam("user", ValidTokenFile().Replace("\n", "\r\n"));

            Connected(steam);

            Assert.Equal(GoodToken, Assert.Single(steam.LogOns).AccessToken);
        }

        [Fact]
        public void AccessTokenDoesNotMatterForSigningIn()
        {
            var steam = CreateSteam("user", $"$RT${GoodToken}\n$AT$\n$SID$");

            Connected(steam);

            Assert.Equal(GoodToken, Assert.Single(steam.LogOns).AccessToken);
        }

        [Theory]
        [InlineData("")]
        [InlineData("$RT$\n$AT$\n$SID$")]
        [InlineData("$RT$not a token\n$AT$not a token\n$SID$0")]
        [InlineData("$RT$a.b.c\n$AT$a.b.c\n$SID$0")]
        [InlineData("$RT$a.bm90IGpzb24.c\n$AT$a.bm90IGpzb24.c\n$SID$0")]
        public void DamagedTokenFileAsksForANewSignIn(string tokenFile)
        {
            var steam = CreateSteam("user", tokenFile);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal(NewToken, Assert.Single(steam.LogOns).AccessToken);
            Assert.Contains($"$RT${NewToken}\n", File.ReadAllText(steam.AuthFullPath));
            Assert.Contains("no usable saved token", Assert.Single(steam.Alerts));
        }

        [Fact]
        public void MissingTokenFileAsksForANewSignIn()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal(NewToken, Assert.Single(steam.LogOns).AccessToken);
            Assert.Contains("no usable saved token", Assert.Single(steam.Alerts));
        }

        [Fact]
        public void ExpiredTokenIsNeverSentToSteam()
        {
            var expired = CreateToken(DateTimeOffset.UtcNow.AddDays(-1));
            var steam = CreateSteam("user", $"$RT${expired}\n$AT${expired}\n$SID$0");
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal(NewToken, Assert.Single(steam.LogOns).AccessToken);
            Assert.Contains("expired on", Assert.Single(steam.Alerts));
        }

        [Fact]
        public void LogOnIsSentOnceWhenTheFirstReadLandsOnAReconnect()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Disconnected(steam);
            Connected(steam);

            Assert.Single(steam.LogOns);
        }

        [Fact]
        public void LogsHoldNoTokenOrClaims()
        {
            var token = CreateToken($$"""{"exp":{{DateTimeOffset.UtcNow.AddDays(100).ToUnixTimeSeconds()}},"sub":"7656","ip_subject":"192.0.2.1"}""");
            var steam = CreateSteam("user", $"$RT${token}\n$AT${token}\n$SID$0");

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.DoesNotContain(token, ConsoleText);
            Assert.DoesNotContain("192.0.2.1", ConsoleText);
            Assert.DoesNotContain("7656", ConsoleText);
            Assert.Contains("expires on", ConsoleText);
        }

        // Failures that leave the bot offline, without crashing it

        [Fact]
        public void MissingUsernameFailsTheSignIn()
        {
            var steam = CreateSteam(null, ValidTokenFile());
            steam.UseRealLogOn = true;

            Connected(steam);

            Assert.Contains(SignInFailed, ConsoleText);
            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void FailedQRSignInDisconnectsToTryAgain()
        {
            var steam = CreateSteam("user", tokenFile: null);

            Connected(steam);

            Assert.Contains(SignInFailed, ConsoleText);
            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void FailedPasswordSignInDisconnectsToTryAgain()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;

            Connected(steam);

            Assert.Contains(SignInFailed, ConsoleText);
            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void PasswordSignInSavesTheTokens()
        {
            var steam = CreateSteam("user", "$RT$\n$AT$\n$SID$");
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal(NewToken, Assert.Single(steam.LogOns).AccessToken);
            Assert.Contains($"$RT${NewToken}\n", File.ReadAllText(steam.AuthFullPath));
        }

        [Fact]
        public void PasswordSignInMovesToQRAfterThreeAttempts()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => throw new AsyncJobFailedException();
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            for (var i = 0; i < 3; i++)
                Connected(steam);

            Assert.Equal(0, steam.QRBegins);

            Connected(steam);

            Assert.Equal(3, steam.PasswordBegins);
            Assert.Equal(1, steam.QRBegins);
            Assert.Equal(NewToken, Assert.Single(steam.LogOns).AccessToken);
        }

        [Fact]
        public void PasswordSignInWhoseTokenIsRejectedMovesToQRAfterThreeAttempts()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());

            for (var i = 0; i < 4; i++)
            {
                Connected(steam);
                LoggedOn(steam, EResult.AccessDenied);
                Disconnected(steam);
            }

            Assert.Equal(3, steam.LogOns.Count);
            Assert.Equal(1, steam.QRBegins);
        }

        [Theory]
        [InlineData(EResult.InvalidPassword)]
        [InlineData(EResult.RateLimitExceeded)]
        [InlineData(EResult.AccountLoginDeniedThrottle)]
        public void WrongPasswordUsesUpAnAttempt(EResult result)
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());
            steam.PasswordBeginFailure = new AuthenticationException("Authentication failed", result);

            for (var i = 0; i < 4; i++)
                Connected(steam);

            Assert.Equal(3, steam.PasswordBegins);
        }

        public static TheoryData<Exception> FailuresBeforeThePasswordIsChecked() =>
        [
            new AsyncJobFailedException(),
            new TaskCanceledException(),
            new AuthenticationException("Authentication failed", EResult.ServiceUnavailable),
            new InvalidOperationException("The SteamClient instance must be connected.")
        ];

        [Theory]
        [MemberData(nameof(FailuresBeforeThePasswordIsChecked))]
        public void FailureBeforeThePasswordIsCheckedUsesUpNoAttempts(Exception failure)
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());
            steam.PasswordBeginFailure = failure;

            for (var i = 0; i < 10; i++)
                Connected(steam);

            Assert.Equal(10, steam.PasswordBegins);
        }

        [Fact]
        public void NoConsoleForSteamGuardMovesToQR()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => throw new InvalidOperationException("No code was provided by the authenticator.");

            Connected(steam);
            Connected(steam);

            Assert.Equal(1, steam.PasswordBegins);
            Assert.Equal(1, steam.QRBegins);
        }

        [Fact]
        public void UnansweredSteamGuardPromptMovesToQR()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => throw new TimeoutException();

            Connected(steam);
            Connected(steam);

            Assert.Equal(1, steam.PasswordBegins);
            Assert.Equal(1, steam.QRBegins);
        }

        [Fact]
        public void SteamGuardAnswerIsGivenTimeBeforeTheDrop()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal(TimeSpan.FromSeconds(55), Assert.Single(steam.WaitLimits));
        }

        [Fact]
        public void PasswordSignInUsesTheSettingNotTheTokensAccount()
        {
            var steam = CreateSteam("configured", $"{ValidTokenFile()}\n$NAME$scanned account");
            steam.isQRAuthenticator = false;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);
            LoggedOn(steam, EResult.Revoked);
            Disconnected(steam);
            Connected(steam);

            Assert.Equal("configured", steam.PasswordUsername);
        }

        [Fact]
        public void GoodLogOnResetsPasswordAttempts()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            var dropping = true;
            steam.PasswordSignIn = () => dropping ? throw new AsyncJobFailedException() : Task.FromResult(NewTokens());

            Connected(steam);
            Connected(steam);

            dropping = false;

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.Revoked);
            Disconnected(steam);

            dropping = true;

            for (var i = 0; i < 3; i++)
                Connected(steam);

            Assert.Equal(0, steam.QRBegins);
        }

        [Fact]
        public void TokenFromASignInLogsOnWithItsAccount()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal("scanned account", Assert.Single(steam.LogOns).Username);
        }

        // What a failed logon does to the token

        // What a failed logon does to the token

        [Theory]
        [InlineData(EResult.Expired)]
        [InlineData(EResult.Revoked)]
        [InlineData(EResult.AccessDenied)]
        [InlineData(EResult.InvalidPassword)]
        [InlineData(EResult.InvalidSignature)]
        public void RejectedTokenIsRetiredAtOnce(EResult result)
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);
            LoggedOn(steam, result);
            Disconnected(steam);

            Assert.True(steam.needsToCreateRefreshToken);
            Assert.Contains($"Unable to logon to Steam: {result}", ConsoleText);
            Assert.Contains(result.ToString(), Assert.Single(steam.Alerts));

            Connected(steam);

            Assert.Equal([GoodToken, NewToken], steam.LogOns.Select(logOn => logOn.AccessToken));
        }

        [Fact]
        public void RejectedTokenMovesOnWithinSeconds()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.Revoked);

            Assert.Equal(1, steam.Disconnects);

            Disconnected(steam);

            Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(steam.Waits));

            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);
        }

        [Fact]
        public void RetiredTokenStaysInTheFileForARestart()
        {
            var file = ValidTokenFile();
            var steam = CreateSteam("user", file);

            Connected(steam);
            LoggedOn(steam, EResult.Expired);

            Assert.Equal(file, File.ReadAllText(steam.AuthFullPath));

            var restarted = CreateSteam("user", tokenFile: null);

            Connected(restarted);

            Assert.Equal(GoodToken, Assert.Single(restarted.LogOns).AccessToken);
        }

        [Theory]
        [InlineData(EResult.TryAnotherCM)]
        [InlineData(EResult.ServiceUnavailable)]
        [InlineData(EResult.Timeout)]
        [InlineData(EResult.RateLimitExceeded)]
        public void TemporaryFailuresNeverRetireTheToken(EResult result)
        {
            var steam = CreateSteam("user", ValidTokenFile());

            for (var i = 0; i < 50; i++)
            {
                Connected(steam);
                LoggedOn(steam, result);
                Disconnected(steam);
            }

            Assert.All(steam.LogOns, logOn => Assert.Equal(GoodToken, logOn.AccessToken));
            Assert.Equal(50, steam.LogOns.Count);
            Assert.Empty(steam.Alerts);
        }

        [Fact]
        public void OtherFailuresKeepTheTokenForADay()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.Fail);
            Disconnected(steam);

            for (var hours = 0; hours < 23; hours++)
            {
                steam.Now += TimeSpan.FromHours(1);

                Connected(steam);
                LoggedOn(steam, EResult.Fail);
                Disconnected(steam);
            }

            Assert.False(steam.needsToCreateRefreshToken);

            steam.Now += TimeSpan.FromHours(1);

            Connected(steam);
            LoggedOn(steam, EResult.Fail);

            Assert.True(steam.needsToCreateRefreshToken);
            Assert.Contains($"rejected the saved token ({EResult.Fail})", steam.Alerts[^1]);
        }

        [Fact]
        public void GoodLogOnRestartsTheDayForOtherFailures()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.Fail);
            Disconnected(steam);

            steam.Now += TimeSpan.FromHours(20);

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            Disconnected(steam);

            steam.Now += TimeSpan.FromHours(20);

            Connected(steam);
            LoggedOn(steam, EResult.Fail);
            Disconnected(steam);

            steam.Now += TimeSpan.FromHours(20);

            Connected(steam);
            LoggedOn(steam, EResult.Fail);

            Assert.False(steam.needsToCreateRefreshToken);
        }

        [Fact]
        public void NewTokenRestartsTheDayForOtherFailures()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);
            LoggedOn(steam, EResult.Fail);
            Disconnected(steam);

            steam.Now += TimeSpan.FromHours(20);

            Connected(steam);
            LoggedOn(steam, EResult.Revoked);
            Disconnected(steam);

            Connected(steam);
            LoggedOn(steam, EResult.Fail);
            Disconnected(steam);

            steam.Now += TimeSpan.FromHours(20);

            Connected(steam);
            LoggedOn(steam, EResult.Fail);

            Assert.False(steam.needsToCreateRefreshToken);
        }

        [Fact]
        public void LogOffDisconnects()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            var loggedOff = (SteamUser.LoggedOffCallback)RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
            loggedOff.JobID = JobID.Invalid;

            Deliver(steam, loggedOff);

            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void ReplyThatNobodyAskedForIsIgnored()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            for (var i = 0; i < 10; i++)
            {
                Connected(steam);
                LoggedOn(steam, EResult.Fail);
                Disconnected(steam);
            }

            Assert.DoesNotContain("Unable to logon", ConsoleText);
        }

        [Fact]
        public void SecondReplyToOneLogOnIsIgnored()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            LoggedOn(steam, EResult.Expired);

            Assert.False(steam.needsToCreateRefreshToken);
            Assert.DoesNotContain("Unable to logon", ConsoleText);
        }

        // Reconnecting

        [Fact]
        public void ReconnectWaitsLongerEachTime()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            for (var i = 0; i < 7; i++)
                Disconnected(steam);

            int[] expected = [30, 60, 120, 240, 480, 900, 900];

            Assert.Equal(7, steam.Connects);

            for (var i = 0; i < expected.Length; i++)
                Assert.InRange(steam.Waits[i].TotalSeconds, expected[i], expected[i] * 1.2);
        }

        [Fact]
        public void GoodLogOnResetsTheReconnectDelay()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            for (var i = 0; i < 4; i++)
                Disconnected(steam);

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);
        }

        [Fact]
        public void ReconnectIsQuickDuringAQRSession()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            for (var i = 0; i < 20; i++)
            {
                Connected(steam);
                Disconnected(steam);
            }

            Assert.All(steam.Waits, wait => Assert.Equal(TimeSpan.FromSeconds(2), wait));
            Assert.DoesNotContain(SignInFailed, ConsoleText);
        }

        [Fact]
        public void QRSessionDoesNotUseUpReconnectAttempts()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            Connected(steam);
            Disconnected(steam);

            steam.QRSignIn = () => throw new AuthenticationException("Failed to poll status", EResult.Expired);

            Connected(steam);
            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);
        }

        [Fact]
        public void QRSessionCarriesAcrossConnections()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            for (var i = 0; i < 5; i++)
            {
                Connected(steam);
                Disconnected(steam);
            }

            Assert.Equal(1, steam.QRBegins);
            Assert.Equal(5, steam.WaitLimits.Count);
        }

        [Fact]
        public void ScanEndsTheQRSession()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);
            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);
        }

        [Fact]
        public void SessionThatSteamEndsIsReplacedOnTheBackoff()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new AuthenticationException("Failed to poll status", EResult.Expired);

            Connected(steam);
            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);

            Connected(steam);

            Assert.Equal(2, steam.QRBegins);
        }

        [Fact]
        public void QRSessionEndsAfterTenMinutes()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            Connected(steam);
            Disconnected(steam);

            steam.Now += TimeSpan.FromMinutes(10);

            Connected(steam);

            Assert.Contains("Nobody scanned the QR code in time", ConsoleText);
            Assert.DoesNotContain("TimeoutException", ConsoleText);

            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);

            Connected(steam);

            Assert.Equal(2, steam.QRBegins);
        }

        [Fact]
        public void QRWaitNeverOutlastsTheSession()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            Connected(steam);
            Disconnected(steam);

            steam.Now += TimeSpan.FromMinutes(9.5);

            Connected(steam);

            Assert.Equal([TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(30)], steam.WaitLimits);
        }

        [Fact]
        public void QRWaitThatTimesOutKeepsTheSession()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TimeoutException();

            Connected(steam);

            Assert.Equal(1, steam.Disconnects);

            Disconnected(steam);

            Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(steam.Waits));
        }

        [Fact]
        public void FailedConnectionsBackOffEvenDuringAQRSession()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => throw new TaskCanceledException();

            Connected(steam);
            Disconnected(steam);

            Assert.Equal(TimeSpan.FromSeconds(2), steam.Waits[^1]);

            for (var i = 0; i < 3; i++)
                Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 120, 144);
        }

        [Fact]
        public void NewSignInResetsTheReconnectDelay()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            for (var i = 0; i < 4; i++)
                Disconnected(steam);

            Connected(steam);
            LoggedOn(steam, EResult.TryAnotherCM);
            Disconnected(steam);

            Assert.InRange(steam.Waits[^1].TotalSeconds, 30, 36);
        }

        [Fact]
        public void ConnectThatThrowsIsTriedAgain()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.ConnectFailures = 2;

            Disconnected(steam);

            Assert.Equal(3, steam.Connects);
            Assert.Equal(3, steam.Waits.Count);
            Assert.InRange(steam.Waits[^1].TotalSeconds, 120, 144);
            Assert.Contains("Connection Retry Failed", ConsoleText);
        }

        // The watchdog

        [Fact]
        public void LogOnThatGetsNoAnswerDisconnects()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            steam.FireStallChecks();

            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void FailedLogOnOnAConnectionSteamLeavesOpenDisconnects()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.Fail);
            steam.FireStallChecks();

            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void AnsweredLogOnKeepsTheConnection()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            steam.FireStallChecks();

            Assert.Equal(0, steam.Disconnects);
        }

        [Fact]
        public void ConnectThatNeverFinishesDisconnects()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Disconnected(steam);
            steam.FireStallChecks();

            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void FirstConnectIsWatchedToo()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            steam.AuthenticateClient();
            steam.FireStallChecks();

            Assert.Equal(1, steam.Connects);
            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public async Task QRWaitIsNotTreatedAsAStall()
        {
            var steam = CreateSteam("user", tokenFile: null);
            var scanned = new TaskCompletionSource<SteamTokens>();
            steam.QRSignIn = () => scanned.Task;

            using var stop = new CancellationTokenSource(WaitLimit);
            var pump = steam.RunCallbacksAsync(stop.Token);

            steam.steamClient.PostCallback(Create<SteamClient.DisconnectedCallback>(false));
            steam.steamClient.PostCallback(Create<SteamClient.ConnectedCallback>());

            await WaitUntil(() => steam.QRBegins == 1);
            steam.FireStallChecks();

            Assert.Equal(0, steam.Disconnects);

            scanned.SetResult(NewTokens());
            await WaitUntil(() => steam.LogOns.Count == 1);

            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        }

        [Fact]
        public void EveryAnswerFromSteamStopsTheWatchdog()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.OK);
            steam.FireStallChecks();

            Assert.Equal(0, steam.Disconnects);
        }

        [Fact]
        public async Task DisconnectStopsTheWatchdogDuringTheWait()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            var reconnecting = new TaskCompletionSource();
            steam.Waiting = reconnecting.Task;

            using var stop = new CancellationTokenSource(WaitLimit);
            var pump = steam.RunCallbacksAsync(stop.Token);

            steam.steamClient.PostCallback(Create<SteamClient.ConnectedCallback>());
            steam.steamClient.PostCallback(Create<SteamClient.DisconnectedCallback>(false));

            await WaitUntil(() => steam.Waits.Count == 1);
            steam.FireStallChecks();

            Assert.Equal(0, steam.Disconnects);

            reconnecting.SetResult();
            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        }

        [Fact]
        public void SteamCallThatNeverAnswersDoesNotStopThePump()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.SteamCallLimit = TimeSpan.FromMilliseconds(20);
            steam.QRBeginWait = new TaskCompletionSource().Task;
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Contains(SignInFailed, ConsoleText);
            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void PasswordCallThatNeverAnswersDoesNotStopThePump()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.isQRAuthenticator = false;
            steam.SteamCallLimit = TimeSpan.FromMilliseconds(20);
            steam.PasswordBeginWait = new TaskCompletionSource().Task;
            steam.PasswordSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Contains(SignInFailed, ConsoleText);
            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public void RenewalThatNeverAnswersDoesNotStopThePump()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SteamCallLimit = TimeSpan.FromMilliseconds(20);
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => new TaskCompletionSource<SteamTokens>().Task;

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Contains("Unable to renew the Steam token", ConsoleText);
        }

        // Telling the owner

        [Fact]
        public void EachIncidentAlertsOnce()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);
            LoggedOn(steam, EResult.Expired);
            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.Expired);
            Disconnected(steam);

            Assert.Single(steam.Alerts);

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(BackAgain, steam.Alerts[^1]);
            Assert.Equal(2, steam.Alerts.Count);

            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.Expired);

            Assert.Equal(3, steam.Alerts.Count);
        }

        [Fact]
        public void GoodLogOnWithNoOpenAlertSendsNothing()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Empty(steam.Alerts);
        }

        [Fact]
        public void OutageOverAnHourAlertsOnce()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            steam.Now += TimeSpan.FromMinutes(59);
            Disconnected(steam);

            Assert.Empty(steam.Alerts);

            steam.Now += TimeSpan.FromMinutes(1);
            Disconnected(steam);
            steam.Now += TimeSpan.FromHours(5);
            Disconnected(steam);

            Assert.Contains("offline from Steam", Assert.Single(steam.Alerts));

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(BackAgain, steam.Alerts[^1]);
        }

        [Fact]
        public void OutageAlertNamesSteamsLastAnswer()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.ServiceUnavailable);
            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);

            Assert.Contains(EResult.ServiceUnavailable.ToString(), Assert.Single(steam.Alerts));
        }

        [Fact]
        public void OutageClockStartsAtTheLastGoodLogOn()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            steam.Now += TimeSpan.FromHours(3);
            LoggedOn(steam, EResult.OK);
            steam.Now += TimeSpan.FromDays(30);

            Disconnected(steam);

            Assert.Empty(steam.Alerts);
        }

        [Fact]
        public void EveryOutageAlertsAgain()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.OK);

            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);
            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);

            Assert.Equal(3, steam.Alerts.Count);
            Assert.Contains("offline from Steam", steam.Alerts[^1]);
        }

        [Fact]
        public void GoodLogOnClearsTheLastAnswer()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.ServiceUnavailable);
            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.OK);
            Disconnected(steam);

            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);

            Assert.DoesNotContain(EResult.ServiceUnavailable.ToString(), Assert.Single(steam.Alerts));
        }

        [Fact]
        public void OutageAfterRunningForWeeksAlertsToo()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            steam.Now += TimeSpan.FromDays(30);
            Disconnected(steam);
            steam.Now += TimeSpan.FromHours(1);
            Disconnected(steam);

            Assert.Contains("offline from Steam", Assert.Single(steam.Alerts));
        }

        [Fact]
        public void SignInAlertStillGoesOutAfterAnOutageAlert()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.Revoked);

            Assert.Equal(2, steam.Alerts.Count);
            Assert.Contains(EResult.Revoked.ToString(), steam.Alerts[^1]);
        }

        [Fact]
        public void OutageAlertIsSkippedWhileASignInAlertIsOpen()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.Revoked);
            steam.Now += TimeSpan.FromHours(2);
            Disconnected(steam);

            Assert.Single(steam.Alerts);
        }

        [Fact]
        public void AlertThatFailsDoesNotStopTheSignIn()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());
            steam.AlertOwner = _ => throw new InvalidOperationException("Discord is down");

            Connected(steam);

            Assert.Single(steam.LogOns);
            Assert.Contains("Unable to alert the owner: Discord is down", ConsoleText);
        }

        [Fact]
        public void SlowAlertDoesNotHoldUpThePump()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());
            steam.AlertOwner = _ => new TaskCompletionSource().Task;

            Connected(steam);

            Assert.Single(steam.LogOns);
        }

        [Fact]
        public void AlertInstructionsMatchTheSignInMethod()
        {
            var qr = CreateSteam("user", tokenFile: null);
            qr.QRSignIn = () => Task.FromResult(NewTokens());
            Connected(qr);
            File.Delete(qr.AuthFullPath);

            var password = CreateSteam("user", tokenFile: null);
            password.isQRAuthenticator = false;
            password.PasswordSignIn = () => Task.FromResult(NewTokens());
            Connected(password);

            Assert.Contains("QR code", Assert.Single(qr.Alerts));
            Assert.Contains("STEAMUSER_PASSWORD", Assert.Single(password.Alerts));
        }

        // Renewing

        [Fact]
        public void TokenIsRenewedOnceAfterTheFirstLogOn()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "access", ""));

            for (var i = 0; i < 3; i++)
            {
                Connected(steam);
                LoggedOn(steam, EResult.OK);
                Disconnected(steam);
            }

            Assert.Equal(1, steam.Renewals);
        }

        [Fact]
        public async Task RenewedTokensAreSavedAndUsed()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(NewTokens());

            Connected(steam);
            await steam.RenewTokenAsync();
            Disconnected(steam);
            Connected(steam);

            Assert.Contains($"$RT${NewToken}\n", File.ReadAllText(steam.AuthFullPath));
            Assert.Equal(NewToken, steam.LogOns[^1].AccessToken);
        }

        [Fact]
        public async Task RenewalWithoutANewRefreshTokenKeepsTheOldOne()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "new access", ""));

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Contains($"$RT${GoodToken}\n", File.ReadAllText(steam.AuthFullPath));
            Assert.False(steam.needsToCreateRefreshToken);
        }

        [Fact]
        public async Task RenewalIsSkippedIfTheFileCantBeSaved()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(NewTokens());

            Connected(steam);
            Directory.CreateDirectory(steam.AuthFullPath + ".tmp");
            await steam.RenewTokenAsync();

            Assert.Equal(0, steam.Renewals);
            Assert.Contains("couldn't save", Assert.Single(steam.Alerts));
        }

        [Fact]
        public async Task RenewalRewritesAnOldStyleFile()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "access", ""));

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Contains("$NAME$user", File.ReadAllText(steam.AuthFullPath));
        }

        [Fact]
        public async Task RenewalIsGivenTheSignedInAccountAndToken()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            (SteamID, string)? asked = null;
            steam.Renewal = (id, token) =>
            {
                asked = (id, token);
                return Task.FromResult(NewTokens());
            };

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Equal((SomeSteamID, GoodToken), asked);
        }

        [Fact]
        public async Task FailedRenewalNeverRetiresTheToken()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => throw new AuthenticationException("Failed to generate token", EResult.Revoked);

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.False(steam.needsToCreateRefreshToken);
            Assert.Contains("Unable to renew the Steam token", ConsoleText);
            Assert.Empty(steam.Alerts);
        }

        [Fact]
        public async Task RenewalWhileSignedOutOnlySendsAReminder()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.SignedIn = null;
            steam.QRSignIn = () => throw new TimeoutException();

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Equal(0, steam.Renewals);
            Assert.Equal(2, steam.Alerts.Count);
            Assert.Contains("still can't sign in", steam.Alerts[^1]);
        }

        [Fact]
        public async Task RenewalDuringAnOutageSendsNothing()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = null;

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Equal(0, steam.Renewals);
            Assert.Empty(steam.Alerts);
        }

        [Fact]
        public async Task RenewalNeverBringsBackARetiredToken()
        {
            var file = ValidTokenFile();
            var steam = CreateSteam("user", file);
            var answer = new TaskCompletionSource<SteamTokens>();
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => answer.Task;

            Connected(steam);

            var renewal = steam.RenewTokenAsync();

            LoggedOn(steam, EResult.Revoked);
            answer.SetResult(NewTokens());
            await renewal.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.True(steam.needsToCreateRefreshToken);
            Assert.DoesNotContain(NewToken, File.ReadAllText(steam.AuthFullPath));
        }

        [Fact]
        public async Task RenewalNeverOverwritesANewerSignIn()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            var answer = new TaskCompletionSource<SteamTokens>();
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => answer.Task;
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            var renewal = steam.RenewTokenAsync();

            LoggedOn(steam, EResult.Revoked);
            Disconnected(steam);
            Connected(steam);
            answer.SetResult(new SteamTokens(null, "old access", CreateToken(DateTimeOffset.UtcNow.AddDays(1))));
            await renewal.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Contains($"$RT${NewToken}\n", File.ReadAllText(steam.AuthFullPath));
        }

        [Theory]
        [InlineData(6, true)]
        [InlineData(8, false)]
        public async Task TokenCloseToExpiryAlertsAfterRenewing(int daysLeft, bool alerts)
        {
            var token = CreateToken(DateTimeOffset.UtcNow.AddDays(daysLeft));
            var steam = CreateSteam("user", $"$RT${token}\n$AT$\n$SID$");
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "access", ""));

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Equal(alerts ? 1 : 0, steam.Alerts.Count);
        }

        [Fact]
        public async Task ExpiryAlertGoesOutOncePerToken()
        {
            var token = CreateToken(DateTimeOffset.UtcNow.AddDays(3));
            var steam = CreateSteam("user", $"$RT${token}\n$AT$\n$SID$");
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "access", ""));

            Connected(steam);
            await steam.RenewTokenAsync();
            await steam.RenewTokenAsync();

            Assert.Contains("expires on", Assert.Single(steam.Alerts));
        }

        [Fact]
        public async Task FailedRenewalStillWarnsAboutExpiry()
        {
            var token = CreateToken(DateTimeOffset.UtcNow.AddDays(3));
            var steam = CreateSteam("user", $"$RT${token}\n$AT$\n$SID$");
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => throw new AuthenticationException("Failed to generate token", EResult.Fail);

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Contains("expires on", Assert.Single(steam.Alerts));
        }

        [Fact]
        public async Task DailyJobNoticesASilentOutage()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            steam.Now += TimeSpan.FromHours(2);
            await steam.RenewTokenAsync();
            await steam.RenewTokenAsync();

            Assert.Contains("offline from Steam", Assert.Single(steam.Alerts));
        }

        [Fact]
        public async Task RenewalThatExtendsTheTokenSendsNoExpiryAlert()
        {
            var token = CreateToken(DateTimeOffset.UtcNow.AddDays(1));
            var steam = CreateSteam("user", $"$RT${token}\n$AT$\n$SID$");
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(NewTokens());

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Empty(steam.Alerts);
        }

        // The token file

        [Fact]
        public void SavedTokensReadBack()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            var restarted = CreateSteam("user", tokenFile: null);

            Connected(restarted);

            var logOn = Assert.Single(restarted.LogOns);

            Assert.Equal(NewToken, logOn.AccessToken);
            Assert.Equal("scanned account", logOn.Username);
            Assert.Empty(restarted.Alerts);
        }

        [Fact]
        public void AccountThatSignedInBeatsTheSetting()
        {
            var steam = CreateSteam("someone else", $"{ValidTokenFile()}\n$NAME$scanned account");

            Connected(steam);

            Assert.Equal("scanned account", Assert.Single(steam.LogOns).Username);
        }

        [Fact]
        public void TokenFileThatCantBeSavedStillSignsIn()
        {
            var file = ValidTokenFile();
            var steam = CreateSteam("user", file);
            var expired = CreateToken(DateTimeOffset.UtcNow.AddDays(-1));
            File.WriteAllText(steam.AuthFullPath, $"$RT${expired}\n$AT$\n$SID$");
            Directory.CreateDirectory(steam.AuthFullPath + ".tmp");
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Equal(NewToken, Assert.Single(steam.LogOns).AccessToken);
            Assert.Contains("couldn't save", steam.Alerts[^1]);
            Assert.Contains(expired, File.ReadAllText(steam.AuthFullPath));
        }

        [Fact]
        public void FailedMoveLeavesNoTempFileBehind()
        {
            var steam = CreateSteam("user", tokenFile: null);
            Directory.CreateDirectory(steam.AuthFullPath);
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Contains("couldn't save", steam.Alerts[^1]);
            Assert.False(File.Exists(steam.AuthFullPath + ".tmp"));
        }

        // The bot's history keeps each alert's text, so the alert names only the exception's type. The library's message,
        // which names the file, goes to the log.
        [Fact]
        public void TokenThatCantBeSavedIsAlertedInTheBotsOwnWords()
        {
            var steam = CreateSteam("user", tokenFile: null);
            Directory.CreateDirectory(steam.AuthFullPath + ".tmp");
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.DoesNotContain(steam.AuthName, steam.Alerts[^1]);
            Assert.Matches(@"^MinerBot couldn't save its Steam token \(\w+Exception\), so the next restart may need a new sign-in\. Its log says why\.$", steam.Alerts[^1]);
            Assert.Contains("Unable to save the Steam token: ", ConsoleText);
            Assert.Contains(steam.AuthName + ".tmp", ConsoleText);
        }

        [Fact]
        public void LeftoverTempFileIsReplaced()
        {
            var steam = CreateSteam("user", tokenFile: null);
            File.WriteAllText(steam.AuthFullPath + ".tmp", "left behind by a crash");
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.Contains($"$RT${NewToken}\n", File.ReadAllText(steam.AuthFullPath));
            Assert.False(File.Exists(steam.AuthFullPath + ".tmp"));
        }

        [Fact]
        public void MissingFolderIsCreated()
        {
            var steam = CreateSteam("user", tokenFile: null);
            steam.AuthPath = Path.Combine(authFolder, "new", "folder");
            steam.QRSignIn = () => Task.FromResult(NewTokens());

            Connected(steam);

            Assert.True(File.Exists(steam.AuthFullPath));
        }

        [Fact]
        public void TokenFileIsPrivateOnLinux()
        {
            if (OperatingSystem.IsWindows())
                return;

            var steam = CreateSteam("user", ValidTokenFile());
            File.SetUnixFileMode(steam.AuthFullPath, (UnixFileMode)Convert.ToInt32("644", 8));
            File.WriteAllText(steam.AuthFullPath + ".tmp", "left behind by a crash");
            File.SetUnixFileMode(steam.AuthFullPath + ".tmp", (UnixFileMode)Convert.ToInt32("644", 8));

            steam.UpdateTokenFile();

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(steam.AuthFullPath));
        }

        // The pump

        [Fact]
        public async Task HandlersRunOneAtATime()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            var reconnecting = new TaskCompletionSource();
            steam.Waiting = reconnecting.Task;

            using var stop = new CancellationTokenSource(WaitLimit);
            var pump = steam.RunCallbacksAsync(stop.Token);

            steam.steamClient.PostCallback(Create<SteamClient.DisconnectedCallback>(false));
            steam.steamClient.PostCallback(Create<SteamClient.ConnectedCallback>());

            await WaitUntil(() => steam.Waits.Count == 1);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            Assert.Empty(steam.LogOns);

            reconnecting.SetResult();

            await WaitUntil(() => steam.LogOns.Count == 1);

            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task PumpKeepsGoingAfterAHandlerFails()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.Waiting = Task.FromException(new InvalidOperationException("broken"));

            using var stop = new CancellationTokenSource(WaitLimit);
            var pump = steam.RunCallbacksAsync(stop.Token);

            steam.steamClient.PostCallback(Create<SteamClient.DisconnectedCallback>(false));
            steam.steamClient.PostCallback(Create<SteamClient.ConnectedCallback>());

            await WaitUntil(() => steam.LogOns.Count == 1);

            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Contains("A Steam callback failed", ConsoleText);
        }

        [Fact]
        public async Task StoppingEndsThePumpAndDisconnects()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            using var stop = new CancellationTokenSource();

            var pump = steam.RunCallbacksAsync(stop.Token);

            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(1, steam.Disconnects);
        }

        [Fact]
        public async Task StoppingInterruptsAReconnectWait()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.UseRealWait = true;
            using var stop = new CancellationTokenSource();

            var pump = steam.RunCallbacksAsync(stop.Token);

            steam.steamClient.PostCallback(Create<SteamClient.DisconnectedCallback>(false));
            await WaitUntil(() => ConsoleText.Contains("reconnecting in"));

            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(0, steam.Connects);
        }

        // The bot's status and history: Steam's state, and an event at each change, never with a secret in it

        static DateOnly? ExpiryOf(string token) => SteamSignInPolicy.ReadTokenExpiry(token) is { } expiry ? DateOnly.FromDateTime(expiry.UtcDateTime) : null;

        static SteamUser.LoggedOffCallback LoggedOffWith(EResult result)
        {
            // It has no constructor that takes a result, and its setter is private
            var loggedOff = (SteamUser.LoggedOffCallback)RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
            loggedOff.JobID = JobID.Invalid;
            typeof(SteamUser.LoggedOffCallback).GetProperty(nameof(SteamUser.LoggedOffCallback.Result))!.SetValue(loggedOff, result);

            return loggedOff;
        }

        [Fact]
        public void SteamIsOfflineFromTheStart()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Assert.Equal(new SteamStatus(SteamConnection.Offline, steam.Now), steam.Status);
            Assert.Empty(steam.Events);
        }

        [Fact]
        public void LogOnsAndDropsAreRecordedWithHowLongEachTook()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            steam.Now += TimeSpan.FromMinutes(2);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(new SteamStatus(SteamConnection.LoggedOn, steam.Now, null, ExpiryOf(GoodToken)), steam.Status);

            steam.Now += TimeSpan.FromMinutes(5);
            Disconnected(steam);
            var dropped = steam.Now;

            Assert.Equal(new SteamStatus(SteamConnection.Offline, dropped, null, ExpiryOf(GoodToken)), steam.Status);

            steam.Now += TimeSpan.FromSeconds(45);
            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(
            [
                "steam.logged_on: the first logon of this run, 120 s after the start",
                "steam.offline: after 300 s logged on",
                $"steam.logged_on: after 45 s offline, since {dropped:yyyy-MM-dd HH:mm:ss} UTC"
            ], steam.Events);
        }

        // Steam then closes the connection, which is the same spell offline
        [Fact]
        public void LogOffIsRecordedOnceWithSteamsReason()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            Deliver(steam, LoggedOffWith(EResult.LogonSessionReplaced));
            Disconnected(steam);

            Assert.Equal(SteamConnection.Offline, steam.Status.State);
            Assert.Equal(["steam.logged_off: LogonSessionReplaced"], steam.Events.Skip(1));
        }

        // A QR wait or an outage tries again and again, so each answer is recorded once until the next good logon. The status
        // shows the last one while the bot is offline.
        [Fact]
        public void EachAnswerToAFailedLogOnIsRecordedOnceASpell()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            var start = steam.Now;

            foreach (var result in new[] { EResult.ServiceUnavailable, EResult.ServiceUnavailable, EResult.TryAnotherCM, EResult.ServiceUnavailable })
            {
                Connected(steam);
                LoggedOn(steam, result);
                Disconnected(steam);
            }

            Assert.Equal(new SteamStatus(SteamConnection.Offline, start, EResult.ServiceUnavailable, ExpiryOf(GoodToken)), steam.Status);

            Connected(steam);
            LoggedOn(steam, EResult.OK);
            Assert.Null(steam.Status.LastLogOnResult);

            Disconnected(steam);
            Connected(steam);
            LoggedOn(steam, EResult.ServiceUnavailable);

            Assert.Equal(["steam.logon_failed: ServiceUnavailable", "steam.logon_failed: TryAnotherCM"], steam.Events.Take(2));
            Assert.StartsWith("steam.logged_on: ", steam.Events[2]);
            Assert.Equal(["steam.offline: after 0 s logged on", "steam.logon_failed: ServiceUnavailable"], steam.Events.Skip(3));
        }

        // Needs sign-in lasts through every disconnect and QR wait until a good logon, and the new token is recorded
        [Fact]
        public void RejectedTokenNeedsASignInUntilTheNextGoodLogOn()
        {
            var steam = CreateSteam("user", ValidTokenFile());

            Connected(steam);
            LoggedOn(steam, EResult.Revoked);
            var needed = steam.Now;

            Assert.Equal(new SteamStatus(SteamConnection.NeedsSignIn, needed, EResult.Revoked), steam.Status);

            steam.Now += TimeSpan.FromMinutes(1);
            Disconnected(steam);
            Connected(steam);
            Disconnected(steam);

            Assert.Equal(new SteamStatus(SteamConnection.NeedsSignIn, needed, EResult.Revoked), steam.Status);

            steam.QRSignIn = () => Task.FromResult(NewTokens());
            Connected(steam);

            Assert.Equal(new SteamStatus(SteamConnection.NeedsSignIn, needed, EResult.Revoked, ExpiryOf(NewToken)), steam.Status);

            LoggedOn(steam, EResult.OK);

            Assert.Equal(new SteamStatus(SteamConnection.LoggedOn, steam.Now, null, ExpiryOf(NewToken)), steam.Status);
            Assert.Equal(
            [
                "steam.logon_failed: Revoked",
                "steam.needs_sign_in: Steam rejected the saved token (Revoked)",
                $"steam.token_saved: expires on {ExpiryOf(NewToken):yyyy-MM-dd}"
            ], steam.Events.Take(3));
            Assert.StartsWith("steam.logged_on: the first logon of this run", steam.Events[3]);
        }

        [Fact]
        public void MissingTokenNeedsASignInOnce()
        {
            var steam = CreateSteam("user", tokenFile: null);

            Connected(steam);
            Disconnected(steam);
            Connected(steam);

            Assert.Equal(SteamConnection.NeedsSignIn, steam.Status.State);
            Assert.Null(steam.Status.TokenExpires);
            Assert.Equal(["steam.needs_sign_in: there's no usable saved token"], steam.Events);
        }

        [Fact]
        public async Task RenewedTokenIsRecorded()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(NewTokens());

            Connected(steam);
            await steam.RenewTokenAsync();

            Assert.Equal(ExpiryOf(NewToken), steam.Status.TokenExpires);
            Assert.Equal([$"steam.token_renewed: expires on {ExpiryOf(NewToken):yyyy-MM-dd}"], steam.Events);

            // Steam doesn't always issue a new one, and then nothing changed
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "access", ""));
            await steam.RenewTokenAsync();

            Assert.Single(steam.Events);
        }

        // The recorder is the bot's, and nothing it does may stop Steam's handlers
        [Fact]
        public void RecorderThatFailsNeverStopsTheSignIn()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.RecordEvent = (_, _) => throw new InvalidOperationException("The recorder failed");

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(SteamConnection.LoggedOn, steam.Status.State);
            Assert.Single(steam.LogOns);
            Assert.Contains("Unable to record a Steam event: InvalidOperationException", ConsoleText);
            Assert.DoesNotContain("The recorder failed", ConsoleText);
        }

        // The account's name, from the setting, the token file or a sign-in, never reaches the console or the error output,
        // through a start from a saved token, a QR sign-in and a password sign-in. Nor does an event hold a name, the SteamID
        // or a token.
        [Fact]
        public void AccountNameNeverReachesTheLog()
        {
            var realError = Console.Error;
            var error = new ConsoleCapture();
            Console.SetError(error);

            try
            {
                // A start from a saved token whose file names one account while the setting names another
                var steam = CreateSteam("ConfiguredAccountName", $"{ValidTokenFile()}\n$NAME$SavedAccountName");
                Connected(steam);
                LoggedOn(steam, EResult.OK);
                Deliver(steam, LoggedOffWith(EResult.LogonSessionReplaced));
                Disconnected(steam);

                // A rejected token, then a QR sign-in
                Connected(steam);
                LoggedOn(steam, EResult.Revoked);
                Disconnected(steam);
                steam.QRSignIn = () => Task.FromResult(new SteamTokens("ScannedAccountName", "new access token", NewToken));
                Connected(steam);
                LoggedOn(steam, EResult.OK);

                // A password sign-in on a fresh start, without the token file the other one left
                File.Delete(steam.AuthFullPath);
                var password = CreateSteam("ConfiguredAccountName", tokenFile: null);
                password.isQRAuthenticator = false;
                password.PasswordSignIn = () => Task.FromResult(new SteamTokens("PasswordAccountName", "new access token", NewToken));
                Connected(password);
                LoggedOn(password, EResult.OK);

                Assert.Equal(["SavedAccountName", "SavedAccountName", "ScannedAccountName"], steam.LogOns.Select(logOn => logOn.Username));
                Assert.Equal("PasswordAccountName", Assert.Single(password.LogOns).Username);
                Assert.Contains("Connected to Steam! Logging in", ConsoleText);

                var everything = ConsoleText + error + string.Join("\n", steam.Events.Concat(password.Events));

                foreach (var secret in new[] { "ConfiguredAccountName", "SavedAccountName", "ScannedAccountName", "PasswordAccountName", "AccountName", GoodToken, NewToken,
                    "new access token", SomeSteamID.ConvertToUInt64().ToString() })
                    Assert.DoesNotContain(secret, everything);
            }
            finally
            {
                Console.SetError(realError);
            }
        }

        // A reconnect the dashboard asks for

        // A logged-on connection is dropped, and the pump logs on again with the saved token after 2 seconds rather than 30,
        // by its own rules. A drop after that waits as long as ever.
        [Fact]
        public void ReconnectOnRequestDisconnectsAndLogsOnAfterTwoSeconds()
        {
            var steam = CreateSteam("user", ValidTokenFile());
            steam.SignedIn = SomeSteamID;
            steam.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "access", ""));

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(SteamReconnect.Started, steam.RequestReconnect());
            Assert.Equal(1, steam.Disconnects);

            // SteamKit's answer to the disconnect
            Disconnected(steam);
            Assert.Equal(TimeSpan.FromSeconds(2), steam.Waits[^1]);

            Connected(steam);
            LoggedOn(steam, EResult.OK);

            Assert.Equal(SteamConnection.LoggedOn, steam.Status.State);
            Assert.Equal(2, steam.LogOns.Count);
            Assert.All(steam.LogOns, logOn => Assert.Equal(GoodToken, logOn.AccessToken));
            Assert.Single(steam.Events, line => line.StartsWith(SteamEvents.Offline + ": "));
            Assert.Equal(2, steam.Events.Count(line => line.StartsWith(SteamEvents.LoggedOn + ": ")));

            Disconnected(steam);
            Assert.True(steam.Waits[^1] >= TimeSpan.FromSeconds(30), $"An ordinary drop waited {steam.Waits[^1]}");
        }

        // A new connection would start a sign-in over, and one while Steam isn't logged on is already what the pump does
        [Fact]
        public async Task ReconnectIsRefusedWhileSigningIn()
        {
            var steam = CreateSteam("user", tokenFile: null);
            var scanned = new TaskCompletionSource<SteamTokens>();
            steam.QRSignIn = () => scanned.Task;

            Assert.Equal(SteamReconnect.NotLoggedOn, steam.RequestReconnect());

            using var stop = new CancellationTokenSource(WaitLimit);
            var pump = steam.RunCallbacksAsync(stop.Token);

            steam.steamClient.PostCallback(Create<SteamClient.ConnectedCallback>());
            await WaitUntil(() => steam.WaitLimits.Count == 1);

            Assert.Equal(SteamReconnect.SigningIn, steam.RequestReconnect());
            Assert.Equal(0, steam.Disconnects);

            scanned.SetResult(NewTokens());
            await WaitUntil(() => steam.LogOns.Count == 1);

            stop.Cancel();
            await pump.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        }

        // Renewed, kept, failed, not saved first or not signed in, with the date the token in use ends
        [Fact]
        public async Task RenewalSaysWhatHappened()
        {
            static DateOnly Ends(string token) => DateOnly.FromDateTime(SteamSignInPolicy.ReadTokenExpiry(token)!.Value.UtcDateTime);

            var renewed = CreateSteam("user", ValidTokenFile());
            renewed.SignedIn = SomeSteamID;
            renewed.Renewal = (_, _) => Task.FromResult(NewTokens());
            Connected(renewed);
            Assert.Equal(new TokenRenewal(TokenRenewalOutcome.Renewed, Ends(NewToken)), await renewed.RenewTokenAsync());

            var kept = CreateSteam("user", ValidTokenFile());
            kept.SignedIn = SomeSteamID;
            kept.Renewal = (_, _) => Task.FromResult(new SteamTokens(null, "new access", ""));
            Connected(kept);
            Assert.Equal(new TokenRenewal(TokenRenewalOutcome.Kept, Ends(GoodToken)), await kept.RenewTokenAsync());

            var failed = CreateSteam("user", ValidTokenFile());
            failed.SignedIn = SomeSteamID;
            failed.Renewal = (_, _) => throw new AuthenticationException("Failed to generate token", EResult.Revoked);
            Connected(failed);
            Assert.Equal(new TokenRenewal(TokenRenewalOutcome.Failed, Ends(GoodToken), nameof(AuthenticationException)), await failed.RenewTokenAsync());

            var signedOut = CreateSteam("user", ValidTokenFile());
            signedOut.SignedIn = null;
            Connected(signedOut);
            Assert.Equal(new TokenRenewal(TokenRenewalOutcome.NotSignedIn), await signedOut.RenewTokenAsync());

            var notSaved = CreateSteam("user", ValidTokenFile());
            notSaved.SignedIn = SomeSteamID;
            notSaved.Renewal = (_, _) => Task.FromResult(NewTokens());
            Connected(notSaved);
            Directory.CreateDirectory(notSaved.AuthFullPath + ".tmp");
            Assert.Equal(new TokenRenewal(TokenRenewalOutcome.NotSaved, Ends(GoodToken)), await notSaved.RenewTokenAsync());
            Assert.Equal(0, notSaved.Renewals);
        }

        // Helpers

        static readonly SteamID SomeSteamID = new(76561198000000000);

        OfflineSteam CreateSteam(string? username, string? tokenFile)
        {
            var steam = new OfflineSteam { AuthPath = authFolder, AuthName = "TestToken.txt" };

            steam.SetCredentials("key", "false", username!, null!);
            steam.CreateClient();

            if (tokenFile != null)
                File.WriteAllText(steam.AuthFullPath, tokenFile);

            return steam;
        }

        static SteamTokens NewTokens() => new("scanned account", "new access token", NewToken);

        static string ValidTokenFile() => $"$RT${GoodToken}\n$AT${GoodToken}\n$SID$0";

        static string CreateToken(DateTimeOffset expires) => CreateToken($$"""{"exp":{{expires.ToUnixTimeSeconds()}}}""");

        static string CreateToken(string payload)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
            return $"header.{encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature";
        }

        static void Connected(OfflineSteam steam) => Deliver(steam, Create<SteamClient.ConnectedCallback>());

        static void Disconnected(OfflineSteam steam) => Deliver(steam, Create<SteamClient.DisconnectedCallback>(false));

        static void LoggedOn(OfflineSteam steam, EResult result) => Deliver(steam, Create<SteamUser.LoggedOnCallback>(result));

        static void Deliver(OfflineSteam steam, CallbackMsg callback)
        {
            steam.steamClient.PostCallback(callback);

            var drained = Task.Run(() =>
            {
                while (steam.manager.RunCallbacks())
                {
                }
            });

            Assert.True(drained.Wait(WaitLimit), "A handler never finished");
        }

        // SteamKit's callbacks have internal constructors
        static T Create<T>(params object[] arguments) where T : CallbackMsg =>
            (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;

        static async Task WaitUntil(Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (!condition())
            {
                Assert.True(clock.Elapsed < WaitLimit, "Timed out");

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        // The watchdog and the alerts log from other threads while tests read the text
        sealed class ConsoleCapture : TextWriter
        {
            readonly ConcurrentQueue<string> parts = new();

            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) => parts.Enqueue(value.ToString());
            public override void Write(string? value) => parts.Enqueue(value ?? "");
            public override string ToString() => string.Concat(parts);
        }

        // Stands in for the connection, and records what the bot asked Steam to do
        sealed class OfflineSteam : SteamAPIAccess
        {
            int disconnects;
            readonly List<TaskCompletionSource> stallDelays = new();

            public readonly List<SteamUser.LogOnDetails> LogOns = new();
            public readonly List<TimeSpan> Waits = new();
            public readonly List<TimeSpan> WaitLimits = new();
            public readonly List<string> Alerts = new();

            // What the bot's recorder was given, as "kind: detail"
            public readonly List<string> Events = new();
            public int Connects, ConnectFailures, Renewals, QRBegins, PasswordBegins;
            public int Disconnects => Volatile.Read(ref disconnects);
            public DateTimeOffset Now = DateTimeOffset.UtcNow;
            public SteamID? SignedIn;
            public string? PasswordUsername;
            public Exception? PasswordBeginFailure;
            public bool UseRealLogOn, UseRealWait;
            public Task Waiting = Task.CompletedTask;
            public Task QRBeginWait = Task.CompletedTask, PasswordBeginWait = Task.CompletedTask;
            public Func<Task<SteamTokens>>? QRSignIn, PasswordSignIn;
            public Func<SteamID, string, Task<SteamTokens>>? Renewal;

            public OfflineSteam()
            {
                AlertOwner = message =>
                {
                    lock (Alerts)
                        Alerts.Add(message);

                    return Task.CompletedTask;
                };

                RecordEvent = (kind, detail) =>
                {
                    lock (Events)
                        Events.Add($"{kind}: {detail}");
                };
            }

            internal override DateTimeOffset UtcNow => Now;
            internal override SteamID? SignedInSteamID => SignedIn;

            internal override void Connect()
            {
                Connects++;

                if (ConnectFailures-- > 0)
                    throw new InvalidOperationException("Connect failed");
            }

            internal override void Disconnect() => Interlocked.Increment(ref disconnects);

            internal override void SendLogOn(SteamUser.LogOnDetails details)
            {
                if (UseRealLogOn)
                    base.SendLogOn(details);
                else
                    LogOns.Add(details);
            }

            internal override Task Wait(TimeSpan delay)
            {
                Waits.Add(delay);

                return UseRealWait ? base.Wait(delay) : Waiting;
            }

            // The checks wait here until a test says their time is up
            internal override Task StallDelay()
            {
                var delay = new TaskCompletionSource();

                lock (stallDelays)
                    stallDelays.Add(delay);

                return delay.Task;
            }

            public void FireStallChecks()
            {
                List<TaskCompletionSource> delays;

                lock (stallDelays)
                {
                    delays = stallDelays.ToList();
                    stallDelays.Clear();
                }

                foreach (var delay in delays)
                    delay.SetResult();
            }

            internal override Task<SteamTokens> GenerateTokens(SteamID steamID, string refreshToken)
            {
                Renewals++;

                return Renewal!(steamID, refreshToken);
            }

            // A session is only passed around here, so it doesn't need to work
            static AuthSession FakeSession<T>() where T : AuthSession => (AuthSession)RuntimeHelpers.GetUninitializedObject(typeof(T));

            internal override async Task<AuthSession> BeginQRSession()
            {
                QRBegins++;

                if (QRSignIn == null)
                    return await base.BeginQRSession();

                await QRBeginWait;

                return FakeSession<QrAuthSession>();
            }

            internal override async Task<AuthSession> BeginPasswordSession(string? username)
            {
                PasswordBegins++;
                PasswordUsername = username;

                if (PasswordSignIn == null)
                    return await base.BeginPasswordSession(username);

                await PasswordBeginWait;

                if (PasswordBeginFailure != null)
                    throw PasswordBeginFailure;

                return FakeSession<CredentialsAuthSession>();
            }

            internal override Task<SteamTokens> WaitForSignIn(AuthSession session, TimeSpan limit)
            {
                WaitLimits.Add(limit);

                return (session is QrAuthSession ? QRSignIn : PasswordSignIn)!();
            }
        }
    }
}
