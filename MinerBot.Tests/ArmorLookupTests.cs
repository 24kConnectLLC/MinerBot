using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DataAccessLibrary.Armor.Models;

namespace MinerBot.Tests
{
    // /armor lookup answers whatever happens to the screenshot, which comes from a local server here.
    // Only the tests where the limit has to fire make it short.
    [Collection("Discord")]
    public class ArmorLookupTests
    {
        const string Image = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nimage";
        const string FetchFailed = "Couldn't fetch the screenshot";

        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);
        static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(200);

        static string Option(string name, string value) => $$"""{"name":"{{name}}","type":3,"value":"{{value}}"}""";

        static ArmorStyle Armor(string screenshot, string description = "An armor") => new()
        {
            ArmorName = "Armor One",
            ArmorDescription = description,
            Author = "Author",
            ArmorChoice = "Bug",
            ArmorScreenshot = screenshot
        };

        static string Url(TcpListener server) => $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/armor.png";

        static Task LookUpAsync(FakeDiscord discord) => discord.SendSlashCommandAsync("armor", "lookup", Option("armor-name", "Armor One"));

        static void AssertSentWithoutAnImage(FakeDiscord discord)
        {
            Assert.Equal(["defer", "GetArmorStyle", "followup"], discord.StepNames);

            var followup = discord.Single("followup");
            var embed = Assert.Single(followup.Message.GetProperty("embeds").EnumerateArray());

            Assert.Equal(["Armor One"], followup.EmbedTitles);
            Assert.Null(followup.File);
            Assert.True(!embed.TryGetProperty("image", out var image) || image.ValueKind == JsonValueKind.Null);
        }

        // Takes one request and sends the answer back. The connection stays open until the test disposes it,
        // so an answer that's cut short stays unfinished.
        static async Task<TcpClient> AnswerAsync(TcpListener server, string answer)
        {
            var client = await server.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var reader = new StreamReader(client.GetStream());
            var writer = new StreamWriter(client.GetStream());

            // The request ends with an empty line
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(TestContext.Current.CancellationToken)))
            {
            }

            await writer.WriteAsync(answer);
            await writer.FlushAsync(TestContext.Current.CancellationToken);

            return client;
        }

        // The first request gets a server error on a connection that's then closed, so the second try comes on a new one
        static async Task<TcpClient> AnswerAfterAServerErrorAsync(TcpListener server, string answer)
        {
            var turnedAway = await AnswerAsync(server, "HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            turnedAway.Dispose();

            return await AnswerAsync(server, answer);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public async Task ArmorWithoutAScreenshotIsSentWithoutAnImage(string? screenshot)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.Armor = Armor(screenshot!);

            await LookUpAsync(discord);

            AssertSentWithoutAnImage(discord);
            Assert.DoesNotContain(FetchFailed, discord.ConsoleText);
        }

        // Nothing is asked for, so nothing fails
        [Theory]
        [InlineData("ftp://example.com/armor.png")]
        [InlineData("file:///etc/passwd")]
        [InlineData("/armor.png")]
        [InlineData("not a link")]
        public async Task ScreenshotThatIsNotAWebLinkIsNotFetched(string screenshot)
        {
            await using var discord = await FakeDiscord.StartAsync();
            discord.Armor.Armor = Armor(screenshot);

            await LookUpAsync(discord);

            AssertSentWithoutAnImage(discord);
            Assert.DoesNotContain(FetchFailed, discord.ConsoleText);
        }

        [Fact]
        public async Task ScreenshotServerThatNeverAnswersIsLeftOut()
        {
            await using var discord = await FakeDiscord.StartAsync();
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            discord.ArmorService.ScreenshotLimit = ShortLimit;
            discord.Armor.Armor = Armor(Url(server));

            await LookUpAsync(discord);

            AssertSentWithoutAnImage(discord);
            Assert.Contains(FetchFailed, discord.ConsoleText);
        }

        // The headers come, and then 5 of the 1,000 bytes they promise
        [Fact]
        public async Task ScreenshotThatStopsHalfwayIsLeftOut()
        {
            await using var discord = await FakeDiscord.StartAsync();
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var answering = AnswerAsync(server, "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\n\r\nimage");
            discord.ArmorService.ScreenshotLimit = ShortLimit;
            discord.Armor.Armor = Armor(Url(server));

            await LookUpAsync(discord);
            using var connection = await answering.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            AssertSentWithoutAnImage(discord);
            Assert.Contains(FetchFailed, discord.ConsoleText);
        }

        [Fact]
        public async Task MissingScreenshotIsLeftOut()
        {
            await using var discord = await FakeDiscord.StartAsync();
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var answering = AnswerAsync(server, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            discord.Armor.Armor = Armor(Url(server));

            await LookUpAsync(discord);
            using var connection = await answering.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            AssertSentWithoutAnImage(discord);
            Assert.Contains(FetchFailed, discord.ConsoleText);
        }

        [Fact]
        public async Task LongDescriptionIsCutToDiscordsLimitForTheImage()
        {
            await using var discord = await FakeDiscord.StartAsync();
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var answering = AnswerAsync(server, Image);
            discord.Armor.Armor = Armor(Url(server), description: new string('a', 2000));

            await LookUpAsync(discord);
            using var connection = await answering.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            var followup = discord.Single("followup");
            var description = Assert.Single(followup.Message.GetProperty("attachments").EnumerateArray()).GetProperty("description").GetString()!;

            Assert.Equal("image", followup.File);
            Assert.Equal(new string('a', 1021) + "...", description);
        }

        // ArmorService fetches with the bot's one web client, which sends a GET again after a server error
        [Fact]
        public async Task ScreenshotIsFetchedAgainAfterAServerError()
        {
            await using var discord = await FakeDiscord.StartAsync();
            using var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            var answering = AnswerAfterAServerErrorAsync(server, Image);
            discord.Armor.Armor = Armor(Url(server));

            await LookUpAsync(discord);
            using var connection = await answering.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);

            Assert.Equal(["defer", "GetArmorStyle", "followup"], discord.StepNames);
            Assert.Equal("image", discord.Single("followup").File);
            Assert.DoesNotContain(FetchFailed, discord.ConsoleText);
        }
    }
}
