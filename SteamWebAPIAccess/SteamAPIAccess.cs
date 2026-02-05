using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SteamWebAPIAccess.Models;

namespace SteamWebAPIAccess
{
    public class SteamAPIAccess : ISteamAPIAccess
    {
        public string PublisherWebAPIKey { get; set; }
        public string VolumeName = "minerbot20";
        public string AuthPath = "data/Auth", AuthName = "SteamToken.txt", refreshTokenSymbol = "$RT$", accessTokenSymbol = "$AT$", steamIDTokenSymbol = "$SID$";
        public string AuthFullPath => AuthPath + "/" + AuthName;
        public bool hasReadAuthFile = false, hasWrittenAuthFile = false, shouldReadAuthFile = true;
        public bool needsToCreateRefreshToken = true;
        public uint appID { get; set; } = uint.Parse(Environment.GetEnvironmentVariable("STEAMAPP_ID"));
        private string _username, _password, _accessToken, _RefreshToken;
        public bool isRunning = false, shouldRememberPassword = false, isQRAuthenticator = false;
        public bool shouldReconnect = false;
        public bool connectAsAnon = false;
        public SteamID _steamID;
        private HttpClient httpClient { get; set; }
        public SteamClient steamClient { get; set; }
        public CallbackManager manager { get; set; }
        public SteamUser steamUser { get; set; }
        public SteamMatchmaking steamMatchmaking { get; private set; }

        string previouslyStoredGuardData = null;

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
            // Create our SteamClient Instance
            steamClient = new SteamClient();

            // Create the Callback Manager which will route callbacks to function calls
            manager = new CallbackManager(steamClient);

            // Get the SteamUser handler, which is used for logging on after successfully connecting
            steamUser = steamClient.GetHandler<SteamUser>();

            steamMatchmaking = steamClient.GetHandler<SteamMatchmaking>();

            // Register a few callbacks we're interested in
            // These are registered upon creaton to a callback manager, which will then route the callbacks to the functions specified
            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            isRunning = true;

            // Initiate the Connection
            steamClient.Connect();

            async void OnLoggedOff(SteamUser.LoggedOffCallback callback)
            {
                Console.WriteLine($"Logged off of Steam: {callback.Result}");
            }

            async void OnLoggedOn(SteamUser.LoggedOnCallback callback)
            {
                if (callback.Result != EResult.OK)
                {
                    Console.WriteLine($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");

                    isRunning = false;
                    return;
                }

                isRunning = true;

                Console.WriteLine("Successfully logged on!");
                // At this point, we'd be able to perform actions on Steam

                //SetCookie(callback); // Doesn't actually do any cookie stuff for now. Just used for clientsteamid.
            }

            async void SetCookie(SteamUser.LoggedOnCallback callback)
            {
                // This is how you concatenate the cookie, you can set it on the Steam domains and it should work
                // but actual usage of this will be left as an excercise for the reader
                var steamLoginSecure = $"{callback.ClientSteamID}||{_accessToken}";

                // Set Timer Job!
                // Then set the cookie to the domain

                // The access token expires in 24 hours (at the time of writing) so you will have to renew it.
                // Parse this token with a JWT library to get the expiration date and setup a timer to renew it.
                // To renew you will have to call this:
                // When allowRenewal is set to true, Steam may return new RefreshToken
                var newTokens = await steamClient.Authentication.GenerateAccessTokenForAppAsync(callback.ClientSteamID, _RefreshToken, allowRenewal: false);

                _accessToken = newTokens.AccessToken;
                _steamID = callback.ClientSteamID;

                if (!string.IsNullOrEmpty(newTokens.RefreshToken))
                {
                    _RefreshToken = newTokens.RefreshToken;
                }

                // Do not forget to update steamLoginSecure with the new accessToken!
            }

            async void OnDisconnected(SteamClient.DisconnectedCallback callback)
            {
                Console.WriteLine($"Disconnected from Steam, reconnecting in 30...");

                try
                {
                    Thread.Sleep(30000);
                    shouldReconnect = true;
                    steamClient.Connect();
                }
                catch
                {
                    Console.WriteLine("Connection Retry Failed!");
                }
            }

            async void OnConnected(SteamClient.ConnectedCallback callback)
            {
                if (connectAsAnon)
                {
                    Console.WriteLine($"Connected to Steam! Logging in as anon");
                    steamUser.LogOnAnonymous();
                    return;
                }

                Console.WriteLine($"Connected to Steam! Logging in {_username}");

                if (shouldReadAuthFile)
                    await SignInWithTokenFile();

                if (shouldReconnect && _RefreshToken != null)
                {
                    // This is when the application has already authenticated and lost connection.             

                    //var newTokens = await steamClient.Authentication.GenerateAccessTokenForAppAsync(_steamID, _RefreshToken, allowRenewal: true);

                    //if (!string.IsNullOrEmpty(newTokens.AccessToken))
                    //{
                    //    _accessToken = newTokens.AccessToken;
                    //}

                    //if (!string.IsNullOrEmpty(newTokens.RefreshToken))
                    //{
                    //    _RefreshToken = newTokens.RefreshToken;
                    //}

                    //UpdateTokenFile();

                    steamUser.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = _username,
                        AccessToken = _RefreshToken,
                        ShouldRememberPassword = shouldRememberPassword
                    });
                    return;
                }

                if (!needsToCreateRefreshToken)
                    return;

                if (isQRAuthenticator)
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

                    // Start polling Steam for authentication response
                    // This response is later used to logon to Steam after connecting
                    var pollResponse = await authSession.PollingWaitForResultAsync();

                    Console.WriteLine($"Logging in as '{pollResponse.AccountName}'...");

                    // Logon to Steam with the access token we have recieved
                    steamUser.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = pollResponse.AccountName,
                        AccessToken = pollResponse.RefreshToken,
                        ShouldRememberPassword = shouldRememberPassword,
                    });

                    _username = pollResponse.AccountName;
                    _accessToken = pollResponse.AccessToken;
                    _RefreshToken = pollResponse.RefreshToken;

                    UpdateTokenFile();

                    // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                    ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                    ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
                }
                else
                {
                    // Begin authenticating via credentials
                    var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                    {
                        Username = _username,
                        Password = _password,
                        IsPersistentSession = shouldRememberPassword,
                        GuardData = previouslyStoredGuardData,
                        /// <see cref="UserConsoleAuthenticator"/> is the default authenticator implementation provided by SteamKit
                        /// for ease of use which blocks the thread and asks for user input to enter the code.
                        /// However, if you require special handling (e.g. you have the TOTP secret and can generate codes on the fly),
                        /// you can implement your own <see cref="SteamKit2.Authentication.IAuthenticator"/>
                        Authenticator = new UserConsoleAuthenticator()
                    });

                    // Starting polling Steam for authentication response
                    var pollResponse = await authSession.PollingWaitForResultAsync();

                    if (pollResponse.NewGuardData != null || pollResponse.NewGuardData != "")
                    {
                        // When using certain two factor methods (such as email 2fa), gaurd data may be provided by Steam
                        // for use in future authentication sessions to avoid triggering 2FA again (this works similarly to the old sentry file system).
                        // Do note that this gaurd data is also a JWT token and has an expiration date.
                        previouslyStoredGuardData = pollResponse.NewGuardData;
                    }

                    // Log on to Steam with the access token we have recieved
                    // Note that we are using RefreshToken for logging on here
                    steamUser.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = pollResponse.AccountName,
                        AccessToken = pollResponse.RefreshToken,
                        ShouldRememberPassword = shouldRememberPassword,
                    });

                    UpdateTokenFile();

                    // This is not required, but it is possible to parse the JWT access token to see the scope and expiration date.
                    ParseJsonWebToken(pollResponse.AccessToken, nameof(pollResponse.AccessToken));
                    ParseJsonWebToken(pollResponse.RefreshToken, nameof(pollResponse.RefreshToken));
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
        }

        // This is simply showing how to parse JWT, this is not required to login to Steam
        private string ParseJsonWebToken(string token, string name)
        {
            // You can use a JWT library to do the parsing for you
            var tokenComponents = token.Split('.');

            // Fix up base64url to normal base64
            var base64 = tokenComponents[1].Replace('-', '+').Replace('_', '/');

            if (base64.Length % 4 != 0)
            {
                base64 += new string('=', 4 - base64.Length % 4);
            }

            var payloadBytes = Convert.FromBase64String(base64);

            // Payload can be parsed as JSON, and then fields such expiration date, scope, etc, can be accessed
            var payload = JsonDocument.Parse(payloadBytes);

            // For brevity we will simply output formatted json to console
            var formatted = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            Console.WriteLine($"{name}: {formatted}");
            Console.WriteLine();

            return formatted;
        }

        public async Task SignInWithTokenFile()
        {
            if (!hasReadAuthFile)
            {
                if (File.Exists(AuthFullPath))
                {
                    var lines = File.ReadLines(AuthFullPath).ToList();
                    foreach (var line in lines)
                    {
                        if (line.StartsWith(refreshTokenSymbol))
                        {
                            _RefreshToken = line.Substring(refreshTokenSymbol.Length);
                        }
                        else if (line.StartsWith(accessTokenSymbol))
                        {
                            _accessToken = line.Substring(accessTokenSymbol.Length);
                        }
                        else if (line.StartsWith(steamIDTokenSymbol))
                        {
                            _steamID = new SteamID(line.Substring(steamIDTokenSymbol.Length));
                        }
                    }

                    string parsedAT = "", parsedRT = "";
                    if (!string.IsNullOrEmpty(_accessToken))
                        parsedAT = ParseJsonWebToken(_accessToken, nameof(_accessToken));

                    if (!string.IsNullOrEmpty(_RefreshToken))
                        parsedRT = ParseJsonWebToken(_RefreshToken, nameof(_RefreshToken));


                    AccessTokenModel dsAT = JsonSerializer.Deserialize<AccessTokenModel>(parsedAT);
                    AccessTokenModel dsRT = JsonSerializer.Deserialize<AccessTokenModel>(parsedRT);

                    DateTime expirationAT = DateTimeOffset.FromUnixTimeSeconds(dsAT.exp).UtcDateTime.ToLocalTime();
                    if (expirationAT > DateTime.UtcNow)
                    {
                        steamUser.LogOn(new SteamUser.LogOnDetails
                        {
                            Username = _username,
                            AccessToken = _RefreshToken, // _accessToken
                            ShouldRememberPassword = shouldRememberPassword
                        });

                        needsToCreateRefreshToken = false;
                    }
                    else
                    {
                        DateTime expirationRT = DateTimeOffset.FromUnixTimeSeconds(dsRT.exp).UtcDateTime.ToLocalTime();
                        if (expirationRT > DateTime.UtcNow)
                        {
                            //var newTokens = await steamClient.Authentication.GenerateAccessTokenForAppAsync(_steamID, _RefreshToken, allowRenewal: true);

                            //if (!string.IsNullOrEmpty(newTokens.AccessToken))
                            //    _accessToken = newTokens.AccessToken;

                            //if (!string.IsNullOrEmpty(newTokens.RefreshToken))
                            //    _RefreshToken = newTokens.RefreshToken;

                            //UpdateTokenFile();

                            steamUser.LogOn(new SteamUser.LogOnDetails
                            {
                                Username = _username,
                                AccessToken = _RefreshToken,
                                ShouldRememberPassword = shouldRememberPassword
                            });

                            needsToCreateRefreshToken = false;
                        }
                        else
                        {
                            needsToCreateRefreshToken = true;
                        }
                    }

                    hasReadAuthFile = true;
                }
                else
                {
                    hasReadAuthFile = false;
                    needsToCreateRefreshToken = true;
                }
            }
        }

        public void UpdateTokenFile()
        {
            // We call this whenever we create a new Access Token or Refresh Token.
            bool selectiveEdit = false;
            string content = $"{refreshTokenSymbol}{_RefreshToken}\n{accessTokenSymbol}{_accessToken}\n{steamIDTokenSymbol}{_steamID}";

            if (File.Exists(AuthFullPath))
            {
                // Should finding a particular line be attempted or.. should we just generate and overwrite everything entirely at once...

                if (selectiveEdit)
                {
                    var lines = File.ReadLines(AuthFullPath).ToList();

                    for (int i = 0; i < lines.Count(); i++)
                    {
                        var linenew = lines[i];

                        if (linenew.StartsWith(refreshTokenSymbol))
                        {
                            if (!string.IsNullOrEmpty(linenew))
                            {
                                if (linenew != _RefreshToken)
                                    lines[i] = refreshTokenSymbol + _RefreshToken;
                            }
                        }
                        else if (linenew.StartsWith(accessTokenSymbol))
                        {
                            if (!string.IsNullOrEmpty(linenew))
                            {
                                if (linenew != _accessToken)
                                    lines[i] = accessTokenSymbol + _accessToken;
                            }
                        }

                        File.WriteAllText(AuthFullPath, lines.ToString());
                    }
                }
                else
                {
                    File.WriteAllText(AuthFullPath, content);
                }

                hasWrittenAuthFile = true;
            }
            else
            {
                Directory.CreateDirectory(AuthPath);
                File.WriteAllText(AuthFullPath, content);
            }
        }

        public void UpdateHandlerCallback()
        {
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }
    }
}
