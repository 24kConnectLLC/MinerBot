using DataAccessLibrary.Discord.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // The bot's own queries on the website's server channel table, unchanged. Made-up servers and channels only.
    [Collection("Database")]
    public class ExistingGuildQueryTests(MariaDbFixture fixture)
    {
        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.GuildTable);
            return db;
        }

        static Guild Server(ulong id, ulong armor = 0, ulong map = 0, ulong lobby = 0, ulong announcements = 0) => new()
        {
            guild_id = id,
            armor_channel_id = armor,
            map_channel_id = map,
            lobby_channel_id = lobby,
            announcements_channel_id = announcements
        };

        [Fact]
        public async Task SaveGuildInsertsThenUpdatesTheSameRow()
        {
            var db = await StartAsync();
            var guilds = db.Guilds();

            await guilds.SaveGuild(Server(1000000000000000001, armor: 2000000000000000001, map: 2000000000000000002, lobby: 2000000000000000003, announcements: 2000000000000000004));
            await guilds.SaveGuild(Server(1000000000000000001, armor: 2000000000000000005, map: Guild.ChannelOff, lobby: 2000000000000000003, announcements: Guild.ChannelOff));

            var saved = Assert.Single(await guilds.GetAllGuilds());
            Assert.Equivalent(Server(1000000000000000001, armor: 2000000000000000005, lobby: 2000000000000000003), saved, strict: true);
        }

        // IDs above 2^63 need the column's UNSIGNED and the model's ulong
        [Fact]
        public async Task BiggestIdsSurvive()
        {
            var db = await StartAsync();
            var guilds = db.Guilds();
            var server = Server(ulong.MaxValue, armor: ulong.MaxValue - 1, map: ulong.MaxValue - 2, lobby: ulong.MaxValue - 3, announcements: ulong.MaxValue - 4);

            await guilds.SaveGuild(server);

            Assert.Equivalent(server, Assert.Single(await guilds.GetAllGuilds()), strict: true);
            Assert.Equivalent(server, await guilds.GetGuild(ulong.MaxValue.ToString()), strict: true);
        }

        // The upsert is one statement on the primary key, so two saves of a new server at once can't both try to insert
        [Fact]
        public async Task TwoSavesOfANewServerAtOnceLeaveOneRow()
        {
            var db = await StartAsync();
            var guilds = db.Guilds();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task SaveWhenReleased(ulong armorChannel)
            {
                await start.Task;
                await guilds.SaveGuild(Server(1000000000000000002, armor: armorChannel));
            }

            var saves = Enumerable.Range(1, 2).Select(i => Task.Run(() => SaveWhenReleased(2000000000000000000 + (ulong)i), TestContext.Current.CancellationToken)).ToArray();
            start.SetResult();

            await Task.WhenAll(saves).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var saved = Assert.Single(await guilds.GetAllGuilds());
            Assert.Contains(saved.armor_channel_id, new[] { 2000000000000000001ul, 2000000000000000002ul });
        }

        // MariaDB compares the string with the BIGINT UNSIGNED column exactly, even for IDs one apart that a double
        // couldn't tell apart
        [Fact]
        public async Task GetGuildFindsExactlyThatServer()
        {
            var db = await StartAsync();
            var guilds = db.Guilds();

            await guilds.SaveGuild(Server(1000000000000000001, armor: 1));
            await guilds.SaveGuild(Server(1000000000000000002, armor: 2));

            Assert.Equal(1ul, (await guilds.GetGuild("1000000000000000001")).armor_channel_id);
            Assert.Equal(2ul, (await guilds.GetGuild("1000000000000000002")).armor_channel_id);
            Assert.Null(await guilds.GetGuild("1000000000000000003"));
        }

        [Fact]
        public async Task DeleteGuildRemovesOnlyThatServer()
        {
            var db = await StartAsync();
            var guilds = db.Guilds();

            await guilds.SaveGuild(Server(1000000000000000001));
            await guilds.SaveGuild(Server(1000000000000000002));

            await guilds.DeleteGuild(1000000000000000001);

            Assert.Equal([1000000000000000002ul], (await guilds.GetAllGuilds()).Select(guild => guild.guild_id));
        }
    }
}
