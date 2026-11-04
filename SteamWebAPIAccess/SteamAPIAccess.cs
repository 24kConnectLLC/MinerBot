using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using System.Text;

namespace SteamWebAPIAccess
{
    public class SteamAPIAccess : ISteamAPIAccess
    {
        // Steam drops a connection that hasn't logged on after about a minute, but a QR session outlives it,
        // so connections are reopened quickly during one to keep the code scannable.
        static readonly TimeSpan QuickReconnectDelay = TimeSpan.FromSeconds(2);
        static readonly TimeSpan QRSessionLength = TimeSpan.FromMinutes(10);

        // Longer than that drop, so a normal connection never reaches it
        static readonly TimeSpan QRConnectionLimit = TimeSpan.FromSeconds(90);

        // Shorter than that drop, because a Steam Guard code typed after it can't be sent
        static readonly TimeSpan PasswordWaitLimit = TimeSpan.FromSeconds(55);

        static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);
        static readonly TimeSpan LongOutage = TimeSpan.FromHours(1);
        static readonly TimeSpan TokenExpiryWarning = TimeSpan.FromDays(7);

        // How long a logon code that says nothing about the token can repeat before the token is treated as dead.
        // It's a day because Steam's weekly maintenance shouldn't cost a working token.
        static readonly TimeSpan OtherFailureLimit = TimeSpan.FromDays(1);

        // Each attempt can send a Steam Guard code or prompt, and repeated failures can lock the account
        const int MaxPasswordAttempts = 3;

        public string PublisherWebAPIKey { get; set; }
        public string AuthPath = "data/Auth", AuthName = "SteamToken.txt", refreshTokenSymbol = "$RT$", accessTokenSymbol = "$AT$", steamIDTokenSymbol = "$SID$", accountNameSymbol = "$NAME$";
        public string AuthFullPath => AuthPath + "/" + AuthName;
        public bool hasReadAuthFile = false;
        public bool needsToCreateRefreshToken => string.IsNullOrEmpty(_RefreshToken);
        public uint appID { get; set; } = uint.Parse(Environment.GetEnvironmentVariable("STEAMAPP_ID"));
        private string? _username, _password, _accessToken, _RefreshToken, _tokenAccount;
        public bool shouldRememberPassword = false, isQRAuthenticator = false;
        public bool connectAsAnon = false;
        public SteamID? _steamID;
        private HttpClient httpClient { get; set; }
        public SteamClient steamClient { get; set; }
        public CallbackManager manager { get; set; }
        public SteamUser steamUser { get; set; }
        public SteamMatchmaking steamMatchmaking { get; private set; }
        public Func<string, Task>? AlertOwner { get; set; }

        // SteamKit's own timeout doesn't cover a call that starts just as the connection drops. Shorter in tests.
        internal TimeSpan SteamCallLimit = TimeSpan.FromSeconds(30);

        string? previouslyStoredGuardData = null;

        // The callback pump runs one handler at a time, so they can't race each other. The daily renewal runs
        // on another thread, so the tokens have a lock. It only reads the alert state, where the worst case is
        // a repeated alert.
        readonly object tokenLock = new();
        CancellationToken stopping;
        AuthSession? qrSession;
        DateTimeOffset qrSessionEnds;
        DateTimeOffset? offlineSince, otherFailuresSince;
        EResult? lastLogOnFailure;
        string? warnedExpiryOf;
        int reconnectAttempts, passwordAttempts, stallCheck;
        bool hasConnected, logOnSent, retryQuickly, hasRenewedToken, alertedSignIn, alertedOutage;

        public void SetCredentials(string key, string shouldRemember, string username, string password)
        {
            PublisherWebAPIKey = key;
            _username = username;
            _password = password;
            if (!bool.TryParse(Environment.GetEnvironmentVariable("STEAM_QRCODE_AUTH").ToLower(), out isQRAuthenticator))
                Console.WriteLine("STEAM_QRCODE_AUTH has an unexpected value. Please choose either 'true' or 'false' for a value.\nAttempting to utilize username & password.");
            if (!bool.TryParse(shouldRemember.ToLower(), out shouldRememberPassword))
                Console.WriteLine("STEAMUSER_REMEMBERME has an unexpected value. Please choose either 'true' or 'false' for a value.");
        }

        public void AuthenticateClient()
        {
            CreateClient();

            // Initiate the Connection
            ConnectToSteam();
        }

        // Separate from connecting, so tests can run the sign-in callbacks offline
        internal void CreateClient()
        {
            // Create our SteamClient Instance
            steamClient = new SteamClient();

            // Create the Callback Manager which will route callbacks to function calls
            manager = new CallbackManager(steamClient);

            // Get the SteamUser handler, which is used for logging on after successfully connecting
            steamUser = steamClient.GetHandler<SteamUser>();

            steamMatchmaking = steamClient.GetHandler<SteamMatchmaking>();

            offlineSince = UtcNow;

            // Register a few callbacks we're interested in
            // These are registered upon creaton to a callback manager, which will then route the callbacks to the functions specified
            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        }

        public async Task RunCallbacksAsync(CancellationToken stoppingToken)
        {
            stopping = stoppingToken;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await manager.RunWaitCallbackAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    Console.WriteLine($"A Steam callback failed: {e}");
                }
            }

            Disconnect();
        }

        // These talk to Steam, so tests replace them

        internal virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        internal virtual SteamID? SignedInSteamID => steamClient.SteamID;
        internal virtual void Disconnect() => steamClient.Disconnect();
        internal virtual void SendLogOn(SteamUser.LogOnDetails details) => steamUser.LogOn(details);
        internal virtual Task Wait(TimeSpan delay) => Task.Delay(delay, stopping);
        internal virtual Task StallDelay() => Task.Delay(StallLimit);

        // SteamKit starts from the same server every time, and our own disconnects don't mark one as bad.
        // Reading the list can wait on Steam, so the first connection leaves that to SteamKit's background fetch.
        internal virtual void Connect() =>
            steamClient.Connect(reconnectAttempts == 0 ? null : SteamSignInPolicy.PickServer(steamClient.Servers.GetAllEndPoints(), Random.Shared));

        internal virtual async Task<SteamTokens> GenerateTokens(SteamID steamID, string refreshToken)
        {
            var tokens = await steamClient.Authentication.GenerateAccessTokenForAppAsync(steamID, refreshToken, allowRenewal: true);

            return new SteamTokens(null, tokens.AccessToken, tokens.RefreshToken);
        }

        internal virtual async Task<AuthSession> BeginQRSession()
        {
            // Start an authentication session by requesting a link
            var authSession = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails()
            { IsPersistentSession = shouldRememberPassword });

            // Steam will periodically refresh the challenge url, this callback allows you to draw a new QR Code
            authSession.ChallengeURLChanged = () =>
            {
                Console.WriteLine();
                Console.WriteLine("Steam has refreshed the challenge URL");

                DrawQRCode(authSession);
            };

            // Draw current QR code right away
            DrawQRCode(authSession);

            return authSession;
        }

        internal virtual async Task<AuthSession> BeginPasswordSession(string? username) =>
            await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = _password,
                IsPersistentSession = shouldRememberPassword,
                GuardData = previouslyStoredGuardData,
                /// <see cref="UserConsoleAuthenticator"/> is the default authenticator implementation provided by SteamKit
                /// for ease of use which blocks the thread and asks for user input to enter the code.
                /// However, if you require special handling (e.g. you have the TOTP secret and can generate codes on the fly),
                /// you can implement your own <see cref="SteamKit2.Authentication.IAuthenticator"/>
                Authenticator = new UserConsoleAuthenticator()
            });

        // This runs off the pump because the password path can block on the console. The limit
        // is there because a poll that starts just as the connection drops can wait forever.
        internal virtual async Task<SteamTokens> WaitForSignIn(AuthSession session, TimeSpan limit)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);

            try
            {
                var pollResponse = await Task.Run(() => session.PollingWaitForResultAsync(wait.Token), wait.Token).WaitAsync(limit, stopping);

                return new SteamTokens(pollResponse.AccountName, pollResponse.AccessToken, pollResponse.RefreshToken, pollResponse.NewGuardData);
            }
            finally
            {
                wait.Cancel();
            }
        }

        async Task<SteamTokens> SignInWithQRCode()
        {
            if (qrSession == null)
            {
                qrSession = await BeginQRSession().WaitAsync(SteamCallLimit, stopping);
                qrSessionEnds = UtcNow + QRSessionLength;
            }

            var timeLeft = qrSessionEnds - UtcNow;

            if (timeLeft <= TimeSpan.Zero)
            {
                qrSession = null;
                throw new TimeoutException("Nobody scanned the QR code in time, so it no longer works");
            }

            try
            {
                // Start polling Steam for authentication response
                // This response is later used to logon to Steam after connecting
                var tokens = await WaitForSignIn(qrSession, timeLeft < QRConnectionLimit ? timeLeft : QRConnectionLimit);

                qrSession = null;

                return tokens;
            }
            catch (AuthenticationException)
            {
                // Steam ended the session
                qrSession = null;
                throw;
            }
        }

        async Task<SteamTokens> SignInWithPassword()
        {
            AuthSession authSession;

            try
            {
                // Begin authenticating via credentials
                authSession = await BeginPasswordSession(_username).WaitAsync(SteamCallLimit, stopping);
            }
            catch (AuthenticationException e) when (SteamSignInPolicy.UsesUpPasswordAttempt(e.Result))
            {
                passwordAttempts++;
                throw;
            }

            // Steam has checked the password now, and may have sent a Steam Guard code or prompt
            passwordAttempts++;

            try
            {
                // Starting polling Steam for authentication response
                var tokens = await WaitForSignIn(authSession, PasswordWaitLimit);

                if (!string.IsNullOrEmpty(tokens.GuardData))
                {
                    // When using certain two factor methods (such as email 2fa), gaurd data may be provided by Steam
                    // for use in future authentication sessions to avoid triggering 2FA again (this works similarly to the old sentry file system).
                    // Do note that this gaurd data is also a JWT token and has an expiration date.
                    previouslyStoredGuardData = tokens.GuardData;
                }

                return tokens;
            }
            catch (Exception e) when (e is TimeoutException || e.Message.Contains("No code"))
            {
                // Nobody is there to answer Steam Guard, so another try would only send another code.
                // A prompt that timed out would also swallow the next code typed at the console.
                passwordAttempts = MaxPasswordAttempts;
                throw;
            }
        }

        void DrawQRCode(QrAuthSession authSession)
        {
            Console.WriteLine($"Challenge URL: {authSession.ChallengeURL}");
            Console.WriteLine();

            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            Console.WriteLine("Use the Steam Mobile App to sign in via QR code:");
            Console.WriteLine(qrCodeAsAsciiArt);
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Console.WriteLine($"Logged off of Steam: {callback.Result}");

            // Nothing would reconnect if Steam left the connection open after this
            Disconnect();
        }

        async Task OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            // Steam sends TryAnotherCM unasked when it drops a connection that hasn't logged on
            if (!logOnSent)
                return;

            logOnSent = false;

            if (callback.Result != EResult.OK)
            {
                Console.WriteLine($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");

                HandleFailedLogOn(callback.Result);

                return;
            }

            StopStallCheck();

            reconnectAttempts = 0;
            passwordAttempts = 0;
            offlineSince = null;
            otherFailuresSince = null;
            lastLogOnFailure = null;
            _steamID = callback.ClientSteamID;

            Console.WriteLine("Successfully logged on!");
            // At this point, we'd be able to perform actions on Steam

            if (alertedSignIn || alertedOutage)
            {
                alertedSignIn = false;
                alertedOutage = false;

                Alert("MinerBot is signed in to Steam again.");
            }

            // Once at startup, so a renewal that doesn't work shows up straight away
            if (!hasRenewedToken)
            {
                hasRenewedToken = true;

                await RenewTokenAsync();
            }
        }

        void HandleFailedLogOn(EResult result)
        {
            lastLogOnFailure = result;

            switch (SteamSignInPolicy.ClassifyLogOnFailure(result))
            {
                case LogOnFailure.Temporary:
                    return;

                case LogOnFailure.Other:
                    otherFailuresSince ??= UtcNow;

                    if (UtcNow - otherFailuresSince < OtherFailureLimit)
                        return;

                    break;
            }

            // The file stays, so a wrong call is undone by a restart, and the next sign-in overwrites it
            lock (tokenLock)
            {
                _accessToken = null;
                _RefreshToken = null;
            }

            NeedsSignIn($"Steam rejected the saved token ({result})");

            // There's nothing to wait for, so whoever is watching gets a QR code in seconds
            retryQuickly = true;
            Disconnect();
        }

        async Task OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            StopStallCheck();

            logOnSent = false;
            offlineSince ??= UtcNow;

            AlertIfOfflineTooLong();

            // A connection that never came up doesn't count, or an outage during a QR session would retry nonstop
            var quickly = retryQuickly || (qrSession != null && hasConnected);

            retryQuickly = false;
            hasConnected = false;

            var delay = quickly ? QuickReconnectDelay : NextReconnectDelay();

            while (true)
            {
                Console.WriteLine($"Disconnected from Steam, reconnecting in {delay.TotalSeconds:0}...");

                await Wait(delay);

                try
                {
                    ConnectToSteam();
                    return;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Connection Retry Failed: {e.Message}");

                    delay = NextReconnectDelay();
                }
            }
        }

        TimeSpan NextReconnectDelay() => SteamSignInPolicy.GetReconnectDelay(reconnectAttempts++, Random.Shared.NextDouble());

        void ConnectToSteam()
        {
            Connect();

            _ = DisconnectIfStalled();
        }

        void AlertIfOfflineTooLong()
        {
            if (alertedSignIn || alertedOutage || !(UtcNow - offlineSince >= LongOutage))
                return;

            alertedOutage = true;

            var lastAnswer = lastLogOnFailure == null ? "" : $" Steam's last answer was {lastLogOnFailure}.";

            Alert($"MinerBot has been offline from Steam for over {LongOutage.TotalHours:0} hour. It keeps trying to reconnect.{lastAnswer}");
        }

        async Task OnConnected(SteamClient.ConnectedCallback callback)
        {
            StopStallCheck();

            hasConnected = true;

            try
            {
                if (connectAsAnon)
                {
                    Console.WriteLine($"Connected to Steam! Logging in as anon");
                    logOnSent = true;
                    steamUser.LogOnAnonymous();
                    return;
                }

                if (!hasReadAuthFile)
                    ReadTokenFile();

                Console.WriteLine($"Connected to Steam! Logging in {_tokenAccount ?? _username}");

                if (needsToCreateRefreshToken)
                {
                    // A QR code needs no password and sends no Steam Guard codes, so it's where password sign-in ends up
                    var tokens = await (isQRAuthenticator || passwordAttempts >= MaxPasswordAttempts ? SignInWithQRCode() : SignInWithPassword());

                    Console.WriteLine($"Logging in as '{tokens.AccountName}'...");

                    _tokenAccount = tokens.AccountName;
                    reconnectAttempts = 0;
                    otherFailuresSince = null;

                    SaveTokens(tokens);
                }

                // Note that we are using RefreshToken for logging on here
                logOnSent = true;

                SendLogOn(new SteamUser.LogOnDetails
                {
                    Username = _tokenAccount ?? _username,
                    AccessToken = _RefreshToken,
                    ShouldRememberPassword = shouldRememberPassword
                });

                _ = DisconnectIfStalled();
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                // A dropped connection isn't a failure during a QR session, which carries on with the next one
                Console.WriteLine(qrSession != null
                    ? $"The connection closed while waiting for the QR code to be scanned: {e.GetType().Name}"
                    : $"Unable to sign in to Steam: {(e is TimeoutException or AuthenticationException ? e.Message : e)}");

                // Every failure leaves through the same reconnect path
                Disconnect();
            }
        }

        // Nothing else notices if Steam goes quiet during a connect or a logon, and the pump would wait forever.
        // This also covers a failed logon, in case Steam doesn't close the connection after one.
        async Task DisconnectIfStalled()
        {
            var check = Interlocked.Increment(ref stallCheck);

            await StallDelay();

            if (check == Volatile.Read(ref stallCheck))
            {
                Console.WriteLine("Steam stopped answering, reconnecting");

                Disconnect();
            }
        }

        void StopStallCheck() => Interlocked.Increment(ref stallCheck);

        void ReadTokenFile()
        {
            string? refreshToken = null, accessToken = null, accountName = null;

            if (File.Exists(AuthFullPath))
            {
                foreach (var line in File.ReadLines(AuthFullPath))
                {
                    if (line.StartsWith(refreshTokenSymbol))
                        refreshToken = line.Substring(refreshTokenSymbol.Length).Trim();
                    else if (line.StartsWith(accessTokenSymbol))
                        accessToken = line.Substring(accessTokenSymbol.Length).Trim();
                    else if (line.StartsWith(accountNameSymbol))
                        accountName = line.Substring(accountNameSymbol.Length).Trim();
                }
            }

            hasReadAuthFile = true;

            var expiry = string.IsNullOrEmpty(refreshToken) ? null : SteamSignInPolicy.ReadTokenExpiry(refreshToken);

            if (expiry == null)
            {
                NeedsSignIn("there's no usable saved token");
            }
            else if (expiry <= UtcNow)
            {
                NeedsSignIn($"the saved token expired on {expiry:yyyy-MM-dd}");
            }
            else
            {
                Console.WriteLine($"The saved Steam token expires on {expiry:yyyy-MM-dd}");

                // The token only works for the account that signed in, whatever STEAMUSER_NAME says
                if (!string.IsNullOrEmpty(accountName))
                    _tokenAccount = accountName;

                lock (tokenLock)
                {
                    _accessToken = accessToken;
                    _RefreshToken = refreshToken;
                }
            }
        }

        void SaveTokens(SteamTokens tokens)
        {
            lock (tokenLock)
            {
                _accessToken = tokens.AccessToken;
                _RefreshToken = tokens.RefreshToken;

                TrySaveTokens();
            }

            Console.WriteLine($"The Steam token expires on {SteamSignInPolicy.ReadTokenExpiry(tokens.RefreshToken):yyyy-MM-dd}");
        }

        bool TrySaveTokens()
        {
            try
            {
                lock (tokenLock)
                    UpdateTokenFile();

                return true;
            }
            catch (Exception e)
            {
                Alert($"MinerBot couldn't save its Steam token, so the next restart may need a new sign-in: {e.Message}");

                return false;
            }
        }

        public async Task RenewTokenAsync()
        {
            string? refreshToken;

            lock (tokenLock)
                refreshToken = _RefreshToken;

            var steamID = SignedInSteamID;

            if (string.IsNullOrEmpty(refreshToken) || steamID == null)
            {
                if (alertedSignIn)
                    Alert($"MinerBot still can't sign in to Steam. {SignInInstructions}");
                else
                    AlertIfOfflineTooLong();

                return;
            }

            // A token that Steam replaces now would be lost, so the file has to work first
            if (!TrySaveTokens())
                return;

            try
            {
                var tokens = await GenerateTokens(steamID, refreshToken).WaitAsync(SteamCallLimit, stopping);

                lock (tokenLock)
                {
                    // Steam only sometimes issues a new one, and a new sign-in or a rejected logon while this ran wins
                    if (!string.IsNullOrEmpty(tokens.RefreshToken) && _RefreshToken == refreshToken)
                        SaveTokens(tokens);
                }
            }
            catch (Exception e)
            {
                // Steam's reason may have nothing to do with logging on, so the token stays until a logon rejects it
                Console.WriteLine($"Unable to renew the Steam token: {e.Message}");
            }

            var token = _RefreshToken ?? refreshToken;
            var expiry = SteamSignInPolicy.ReadTokenExpiry(token);

            if (expiry - UtcNow < TokenExpiryWarning && token != warnedExpiryOf)
            {
                warnedExpiryOf = token;

                Alert($"MinerBot's Steam token expires on {expiry:yyyy-MM-dd} and Steam hasn't renewed it. To replace it, rename {AuthFullPath}, restart the bot and sign in.");
            }
        }

        void NeedsSignIn(string reason)
        {
            if (alertedSignIn)
            {
                Console.WriteLine($"Steam sign-in needed: {reason}");
                return;
            }

            alertedSignIn = true;

            Alert($"MinerBot can't sign in to Steam: {reason}. {SignInInstructions}");
        }

        string SignInInstructions => isQRAuthenticator
            ? $"Lobbies stay offline until someone signs in. Restart the bot if its log isn't showing a QR code, then scan the newest code with the Steam Mobile App. If a restart shows no code, rename {AuthFullPath} and restart again."
            : $"Lobbies stay offline until someone signs in. The bot tries STEAMUSER_PASSWORD up to {MaxPasswordAttempts} times, which may need a Steam Guard code on its console, then shows a QR code in its log to scan with the Steam Mobile App.";

        // Not awaited, so a slow Discord can't hold up the callback pump
        void Alert(string message)
        {
            Console.WriteLine(message);

            _ = SendAlert(message);
        }

        async Task SendAlert(string message)
        {
            try
            {
                if (AlertOwner != null)
                    await AlertOwner(message);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unable to alert the owner: {e.Message}");
            }
        }

        public void UpdateTokenFile()
        {
            // We call this whenever we create a new Access Token or Refresh Token.
            string content = $"{refreshTokenSymbol}{_RefreshToken}\n{accessTokenSymbol}{_accessToken}\n{steamIDTokenSymbol}{_steamID}\n{accountNameSymbol}{_tokenAccount ?? _username}";
            string tempPath = AuthFullPath + ".tmp";

            Directory.CreateDirectory(AuthPath);

            // A leftover temp file would keep its old permissions
            File.Delete(tempPath);

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };

            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            try
            {
                using (var file = new FileStream(tempPath, options))
                {
                    file.Write(Encoding.UTF8.GetBytes(content));
                    file.Flush(flushToDisk: true);
                }

                // A crash up to here leaves the real file as it was
                File.Move(tempPath, AuthFullPath, overwrite: true);
            }
            finally
            {
                File.Delete(tempPath);
            }
        }
    }

    internal sealed record SteamTokens(string? AccountName, string AccessToken, string RefreshToken, string? GuardData = null);
}
