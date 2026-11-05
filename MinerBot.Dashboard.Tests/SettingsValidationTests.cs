using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MinerBot.Dashboard.Hosting;
using MinerBot.Dashboard.Settings;
using MinerBot.Dashboard.Tests.Support;

namespace MinerBot.Dashboard.Tests
{
    // The settings are checked as the app starts, through the same registrations and the same check Program.cs uses, so a
    // missing or wrong value stops it with a message that names the key. No database is needed.
    public sealed class SettingsValidationTests : IDisposable
    {
        // Made up
        const string ProxyKey = "0f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a69780f1e2d3c4b5a6978";
        const string ClientSecret = "made-up-client-secret-5c1e0a7d";
        const string SitePassword = "made-up-site-password-41d9";
        const string BotPassword = "made-up-bot-password-8e27";

        readonly string keysFolder = DashboardFactory.NewKeysFolder();

        public void Dispose() => DashboardFactory.DeleteFolder(keysFolder);

        Dictionary<string, string?> Valid() => new()
        {
            ["Dashboard:PublicOrigin"] = "https://dashboard.test",
            ["Dashboard:ProxyKey"] = ProxyKey,
            ["Dashboard:Owners"] = "1000000000000000001",
            ["Dashboard:Admins"] = "1000000000000000002,1000000000000000003",
            ["Dashboard:KeysFolder"] = keysFolder,
            ["Discord:ClientId"] = "1000000000000000009",
            ["Discord:ClientSecret"] = ClientSecret,
            ["ConnectionStrings:DefaultConnection"] = $"Server=127.0.0.1;Database=site_db;Uid=site_login;Pwd={SitePassword}",
            ["ConnectionStrings:BotConnection"] = $"Server=127.0.0.1;Database=bot_db;Uid=bot_login;Pwd={BotPassword}"
        };

        static ServiceProvider Provider(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            return new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddDashboardSettings().BuildServiceProvider();
        }

        static IReadOnlyList<string> Problems(Dictionary<string, string?> settings)
        {
            using var provider = Provider(settings);
            return DashboardHosting.SettingsProblems(provider);
        }

        [Fact]
        public void ValidSettingsPass()
        {
            Assert.Empty(Problems(Valid()));
        }

        public static TheoryData<string, bool> RequiredKeys => new()
        {
            { "Discord:ClientId", true }, { "Discord:ClientId", false },
            { "Discord:ClientSecret", true }, { "Discord:ClientSecret", false },
            { "Dashboard:ProxyKey", true }, { "Dashboard:ProxyKey", false },
            { "Dashboard:PublicOrigin", true }, { "Dashboard:PublicOrigin", false },
            { "Dashboard:Owners", true }, { "Dashboard:Owners", false },
            { "Dashboard:KeysFolder", true }, { "Dashboard:KeysFolder", false },
            { "ConnectionStrings:DefaultConnection", true }, { "ConnectionStrings:DefaultConnection", false },
            { "ConnectionStrings:BotConnection", true }, { "ConnectionStrings:BotConnection", false }
        };

        // Left out, or there but empty. Fails if any check is dropped.
        [Theory]
        [MemberData(nameof(RequiredKeys))]
        public void MissingKeyIsNamed(string key, bool leftOut)
        {
            var settings = Valid();

            if (leftOut)
                settings.Remove(key);
            else
                settings[key] = "";

            var problem = Assert.Single(Problems(settings));
            Assert.StartsWith(key + " ", problem);
        }

        public static TheoryData<string, string> BadValues => new()
        {
            { "Dashboard:Owners", "12ab" },
            { "Dashboard:Owners", "123" },
            { "Dashboard:Owners", "test_owner" },
            { "Dashboard:Admins", "1000000000000000002,<@1000000000000000003>" },
            { "Dashboard:PublicOrigin", "http://bot.example.test" },
            { "Dashboard:PublicOrigin", "https://dashboard.test/admin" },
            { "Dashboard:PublicOrigin", "https://dashboard.test/?x=1" },
            { "Dashboard:PublicOrigin", "https://someone@dashboard.test" },
            { "Dashboard:PublicOrigin", "dashboard.test" },
            { "Dashboard:ListenAddress", "0.0.0.0" },
            { "Dashboard:ListenAddress", "203.0.113.5" },
            { "Dashboard:ListenAddress", "localhost" },
            { "Dashboard:Port", "0" },
            { "Dashboard:Port", "70000" },
            { "Dashboard:Port", "5080x" },
            { "Discord:AuthorizationEndpoint", "http://discord.com/oauth2/authorize" },
            { "Discord:TokenEndpoint", "http://discord.com/api/oauth2/token" },
            { "Discord:RevocationEndpoint", "https://user:password@discord.com/api/oauth2/token/revoke" },
            { "Discord:UserInformationEndpoint", "users/@me" },
            { "Dashboard:ProxyKey", "0f1e2d3c4b5a69780f1e2d3c4b5a697" },
            { "Dashboard:ProxyKey", "0f1e2d3c4b5a6978 0f1e2d3c4b5a69780f1e2d3c4b5a6978" },
            // nginx would read these as a variable, or as escapes, and send something else
            { "Dashboard:ProxyKey", "0f1e2d3c4b5a6978$host0f1e2d3c4b5a69780f1e2d3c4b5a6978" },
            { "Dashboard:ProxyKey", "0f1e2d3c4b5a6978\"0f1e2d3c4b5a69780f1e2d3c4b5a6978" },
            { "Dashboard:ProxyKey", "0f1e2d3c4b5a6978\\0f1e2d3c4b5a69780f1e2d3c4b5a6978" },
            { "Dashboard:ProxyKey", "0f1e2d3c4b5a6978!0f1e2d3c4b5a69780f1e2d3c4b5a6978" },
            // One the readiness check would pass, since SELECT 1 needs no database, and then every sign-in would fail
            { "ConnectionStrings:BotConnection", "Server=127.0.0.1;Uid=bot_login;Pwd=made-up-bot-password-8e27" },
            { "ConnectionStrings:DefaultConnection", "Server=127.0.0.1;Uid=site_login;Pwd=made-up-site-password-41d9" },
            { "ConnectionStrings:DefaultConnection", "Server=127.0.0.1;Database=site_db;SslMode=Requird;Uid=site_login" },
            { "ConnectionStrings:BotConnection", "Server=127.0.0.1;Database=bot\\db;Uid=bot_login" },
            { "Dashboard:KeysFolder", "relative-keys" },
            { "Discord:ClientId", "MinerBot Dashboard" },
            { "Discord:ClientId", "123" },
            { "Discord:ClientSecret", "made-up secret" }
        };

        // Each is refused with a message of its own that names the key, and never repeats the value
        [Theory]
        [MemberData(nameof(BadValues))]
        public void BadValueIsRefused(string key, string value)
        {
            var settings = Valid();
            settings[key] = value;

            var problem = Assert.Single(Problems(settings));
            Assert.StartsWith(key + " ", problem);

            if (value.Length >= 4)
                Assert.DoesNotContain(value, problem);
        }

        [Fact]
        public void ThirtyOneCharacterProxyKeyIsTooShort()
        {
            var settings = Valid();
            settings["Dashboard:ProxyKey"] = new string('a', 31);

            Assert.Contains("shorter than 32", Assert.Single(Problems(settings)));

            settings["Dashboard:ProxyKey"] = new string('a', 32);
            Assert.Empty(Problems(settings));
        }

        [Fact]
        public void IdInBothListsIsRefused()
        {
            var settings = Valid();
            settings["Dashboard:Admins"] = "1000000000000000002,1000000000000000001";

            var problem = Assert.Single(Problems(settings));
            Assert.Contains("Dashboard:Owners", problem);
            Assert.Contains("Dashboard:Admins", problem);
            Assert.DoesNotContain("1000000000000000001", problem);
        }

        [Fact]
        public void KeysFolderThatIsAFileIsRefused()
        {
            Directory.CreateDirectory(keysFolder);
            var file = Path.Combine(keysFolder, "not-a-folder");
            File.WriteAllText(file, "");

            var settings = Valid();
            settings["Dashboard:KeysFolder"] = file;

            Assert.StartsWith("Dashboard:KeysFolder names a file", Assert.Single(Problems(settings)));
        }

        // Made if it's missing, and the probe file is gone afterwards, so Data Protection never sees it
        [Fact]
        public void KeysFolderIsMadeAndLeftEmpty()
        {
            var nested = Path.Combine(keysFolder, "nested", "keys");
            var settings = Valid();
            settings["Dashboard:KeysFolder"] = nested;

            Assert.Empty(Problems(settings));
            Assert.True(Directory.Exists(nested));
            Assert.Empty(Directory.EnumerateFileSystemEntries(nested));
        }

        // A folder the dashboard can't write to stops it at start-up rather than at the first sign-in. A folder where the
        // probe file would go makes the write fail on any system, without changing anyone's permissions.
        [Fact]
        public void KeysFolderThatCantBeWrittenIsRefused()
        {
            Directory.CreateDirectory(Path.Combine(keysFolder, DashboardOptionsValidator.ProbeName));

            Assert.StartsWith("Dashboard:KeysFolder can't be written to", Assert.Single(Problems(Valid())));
        }

        [Theory]
        [InlineData("Dashboard:ListenAddress", "127.0.0.1")]
        [InlineData("Dashboard:ListenAddress", "127.0.0.2")]
        [InlineData("Dashboard:ListenAddress", "::1")]
        [InlineData("Dashboard:Port", "1")]
        [InlineData("Dashboard:Port", "65535")]
        [InlineData("Dashboard:Port", "")]
        [InlineData("Dashboard:Admins", "")]
        [InlineData("Dashboard:Owners", "1000000000000000001 1000000000000000004")]
        [InlineData("Dashboard:PublicOrigin", "http://localhost:5080")]
        [InlineData("Dashboard:ProxyKey", "base64url-key_0123456789ABCDEFabcdef-_xyz")]
        [InlineData("ConnectionStrings:BotConnection", "Server=127.0.0.1;Initial Catalog=bot_db;Uid=bot_login")]
        public void OtherGoodValuesPass(string key, string value)
        {
            var settings = Valid();
            settings[key] = value;

            Assert.Empty(Problems(settings));
        }

        [Theory]
        [InlineData("https://Dashboard.Test", "https://dashboard.test")]
        [InlineData("https://dashboard.test/", "https://dashboard.test")]
        [InlineData("https://dashboard.test:443", "https://dashboard.test")]
        [InlineData("https://dashboard.test:8443/", "https://dashboard.test:8443")]
        [InlineData(" https://dashboard.test ", "https://dashboard.test")]
        [InlineData("http://localhost:5080/", "http://localhost:5080")]
        public void PublicOriginIsKeptAsSchemeAndHost(string given, string kept)
        {
            var settings = Valid();
            settings["Dashboard:PublicOrigin"] = given;

            using var provider = Provider(settings);

            Assert.Empty(DashboardHosting.SettingsProblems(provider));
            Assert.Equal(kept, provider.GetRequiredService<IOptions<DashboardOptions>>().Value.PublicOrigin);
        }

        // Everything wrong at once is reported at once, and no message holds any of the secrets that were set
        [Fact]
        public void EveryProblemIsReportedAndNoneRepeatsASecret()
        {
            var settings = Valid();
            settings["Dashboard:PublicOrigin"] = "http://bot.example.test/admin";
            settings["Dashboard:Owners"] = "12ab";
            settings["Dashboard:Admins"] = "test_admin";
            settings["Dashboard:KeysFolder"] = "relative-keys";
            settings["Dashboard:ListenAddress"] = "0.0.0.0";
            settings["Dashboard:Port"] = "0";
            settings["Discord:ClientId"] = "x";
            settings["Discord:ClientSecret"] = ClientSecret + " " + ClientSecret;
            settings["Discord:TokenEndpoint"] = "http://discord.com/api/oauth2/token";
            settings["Dashboard:ProxyKey"] = ProxyKey + " ";

            var problems = Problems(settings);

            Assert.Equal(10, problems.Count);

            foreach (var secret in new[] { ProxyKey, ClientSecret, SitePassword, BotPassword, "site_login", "bot_login" })
                Assert.All(problems, problem => Assert.DoesNotContain(secret, problem));
        }

        // Kestrel and the healthcheck command read the address before the settings are checked, so it never throws
        [Theory]
        [InlineData(null, null, "127.0.0.1:5080")]
        [InlineData("", "", "127.0.0.1:5080")]
        [InlineData("127.0.0.2", null, "127.0.0.2:5080")]
        [InlineData("::1", "5081", "[::1]:5081")]
        public void ListenEndpointComesFromTheSettingsOrTheDefaults(string? address, string? port, string endpoint)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:ListenAddress"] = address,
                ["Dashboard:Port"] = port
            }).Build();

            Assert.Equal(endpoint, DashboardSettings.ListenEndpoint(configuration)?.ToString());
        }

        [Theory]
        [InlineData("localhost", null)]
        [InlineData(null, "port")]
        [InlineData(null, "0")]
        [InlineData(null, "70000")]
        public void ListenEndpointThatDoesNotParseIsNull(string? address, string? port)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:ListenAddress"] = address,
                ["Dashboard:Port"] = port
            }).Build();

            Assert.Null(DashboardSettings.ListenEndpoint(configuration));
        }

        // Made up, as setup-dashboard.sh writes them
        const string TeamDomain = "made-up-team.cloudflareaccess.com";
        const string Audience = "4714c1358e65fe4b408ad6d432a5f878f08194bdb4752441fd56faefa9b2b6f2";

        Dictionary<string, string?> WithAccess(string? teamDomain = TeamDomain, string? audience = Audience)
        {
            var settings = Valid();
            settings["CloudflareAccess:TeamDomain"] = teamDomain;
            settings["CloudflareAccess:Audience"] = audience;
            return settings;
        }

        // Both empty or both left out is the check turned off, and both set in their form turns it on
        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData(TeamDomain, Audience)]
        [InlineData("a.cloudflareaccess.com", Audience)]
        [InlineData("0-9.cloudflareaccess.com", Audience)]
        public void AccessSettingsThatPass(string? teamDomain, string? audience)
        {
            Assert.Empty(Problems(WithAccess(teamDomain, audience)));
        }

        // The longest team name DNS allows passes, and one more character doesn't
        [Fact]
        public void TeamNameOf63CharactersPasses()
        {
            Assert.Empty(Problems(WithAccess(new string('a', 63) + ".cloudflareaccess.com")));
            Assert.StartsWith("CloudflareAccess:TeamDomain ", Assert.Single(Problems(WithAccess(new string('a', 64) + ".cloudflareaccess.com"))));
        }

        [Theory]
        [InlineData(TeamDomain, null)]
        [InlineData(TeamDomain, "")]
        [InlineData(null, Audience)]
        [InlineData("", Audience)]
        public void OneWithoutTheOtherIsRefused(string? teamDomain, string? audience)
        {
            var problem = Assert.Single(Problems(WithAccess(teamDomain, audience)));

            Assert.Equal("CloudflareAccess:TeamDomain and CloudflareAccess:Audience go together: set both to turn the Cloudflare Access check on, or neither to leave it off.", problem);
        }

        public static TheoryData<string> BadTeamDomains => new()
        {
            "https://made-up-team.cloudflareaccess.com",
            "Made-Up-Team.cloudflareaccess.com",
            "made-up-team.cloudflareaccess.org",
            "made-up-team.example.com",
            "made-up-team.cloudflareaccess.com/",
            "made-up-team.cloudflareaccess.com\n",
            "-made-up-team.cloudflareaccess.com",
            "made-up-team-.cloudflareaccess.com",
            "made_up_team.cloudflareaccess.com",
            "made up team.cloudflareaccess.com",
            "made-up-team",
            "cloudflareaccess.com",
            "team.made-up.cloudflareaccess.com"
        };

        // Each says what's wrong without repeating it
        [Theory]
        [MemberData(nameof(BadTeamDomains))]
        public void BadTeamDomainIsRefused(string teamDomain)
        {
            var problem = Assert.Single(Problems(WithAccess(teamDomain)));

            Assert.Equal("CloudflareAccess:TeamDomain must be just the team's host name in small letters, such as yourteam.cloudflareaccess.com.", problem);
            Assert.DoesNotContain("made", problem);
        }

        public static TheoryData<string> BadAudiences => new()
        {
            Audience[..63],
            Audience + "0",
            Audience.ToUpperInvariant(),
            Audience[..63] + "g",
            " " + Audience[..63],
            Audience[..62] + "\n"
        };

        [Theory]
        [MemberData(nameof(BadAudiences))]
        public void BadAudienceIsRefused(string audience)
        {
            var problem = Assert.Single(Problems(WithAccess(audience: audience)));

            Assert.Equal("CloudflareAccess:Audience must be the Access application's AUD tag: 64 characters, 0-9 and a-f.", problem);
            Assert.DoesNotContain(Audience[..16], problem, StringComparison.OrdinalIgnoreCase);
        }

        // Every problem at once: one without the other, and what's wrong with the one that's there
        [Fact]
        public void BadTeamDomainWithoutAudienceReportsBoth()
        {
            var problems = Problems(WithAccess("https://" + TeamDomain, null));

            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, problem => problem.Contains("go together", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("CloudflareAccess:TeamDomain must be", StringComparison.Ordinal));
            Assert.All(problems, problem => Assert.DoesNotContain(TeamDomain, problem));
        }

        // The Access section is checked with the other two, so a start, and check-settings, report all three
        [Fact]
        public void AllThreeSectionsFailingReportsEach()
        {
            var settings = WithAccess(audience: "not-an-aud-tag");
            settings["Dashboard:ProxyKey"] = "";
            settings["Discord:ClientSecret"] = "";

            var problems = Problems(settings);

            Assert.Equal(3, problems.Count);
            Assert.Contains(problems, problem => problem.StartsWith("CloudflareAccess:Audience ", StringComparison.Ordinal));
            Assert.All(problems, problem => Assert.DoesNotContain("not-an-aud-tag", problem));
        }

        [Fact]
        public void BothSectionsFailingReportsBoth()
        {
            var settings = Valid();
            settings["Dashboard:ProxyKey"] = "";
            settings["Discord:ClientSecret"] = "";

            var problems = Problems(settings);

            Assert.Equal(2, problems.Count);
            Assert.Contains(problems, problem => problem.StartsWith("Dashboard:ProxyKey ", StringComparison.Ordinal));
            Assert.Contains(problems, problem => problem.StartsWith("Discord:ClientSecret ", StringComparison.Ordinal));
        }
    }
}
