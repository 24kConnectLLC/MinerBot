using DataAccessLibrary.Audit;
using DataAccessLibrary.Posting.Models;
using DataAccessLibrary.Tests.Database;

namespace DataAccessLibrary.Tests
{
    // Each server's switches as the dashboard saves them: a save applies only to the switches its form was drawn from, so a
    // form drawn before another save can't undo it, and two first saves at once can't both land
    [Collection("Database")]
    public class GuildSwitchDataTests(MariaDbFixture fixture)
    {
        static readonly DateTime Noon = new(2026, 11, 24, 12, 0, 0, 789, DateTimeKind.Utc);
        static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
        const ulong GuildId = 1000000000000000001, OwnerId = 1000000000000000009;

        async Task<MariaDbFixture> StartAsync()
        {
            var db = fixture.Require();
            await db.ClearAsync(MariaDbFixture.SwitchTable);
            return db;
        }

        static SwitchChange Change(bool armor, bool maps, bool lobbies, uint version, DateTime at) => new()
        {
            GuildId = GuildId,
            ArmorOn = armor,
            MapsOn = maps,
            LobbiesOn = lobbies,
            Version = version,
            At = at,
            ChangedBy = OwnerId,
            Role = AuditRole.Owner
        };

        static Task<SwitchSave> SaveAsync(MariaDbFixture db, SwitchChange change)
        {
            var switches = db.Switches();
            return switches.RunInTransaction(transaction => switches.SaveSwitches(change, transaction));
        }

        // A form drawn before a save, with no switches or with the version before, is refused, and the switches stay as the
        // save left them. Fails for a save without the version check.
        [Fact]
        public async Task SaveRefusesAStaleVersion()
        {
            var db = await StartAsync();

            var first = await SaveAsync(db, Change(false, true, true, 0, Noon));
            Assert.Equal(SwitchSaveOutcome.Saved, first.Outcome);
            Assert.Null(first.Before);
            Assert.Equal(new GuildSwitches { guild_id = GuildId, armor_on = false, maps_on = true, lobbies_on = true, version = 1, changed_at = Noon, changed_by = OwnerId,
                changed_by_role = AuditRole.Owner }, first.After);

            var drawnBeforeIt = await SaveAsync(db, Change(true, false, true, 0, Noon.AddMinutes(1)));
            Assert.Equal(new SwitchSave(SwitchSaveOutcome.Stale, first.After, null), drawnBeforeIt);

            var second = await SaveAsync(db, Change(true, false, true, 1, Noon.AddMinutes(2)));
            Assert.Equal((SwitchSaveOutcome.Saved, first.After, 2u), (second.Outcome, second.Before, second.After!.version));

            Assert.Equal(SwitchSaveOutcome.Stale, (await SaveAsync(db, Change(false, false, false, 1, Noon.AddMinutes(3)))).Outcome);
            Assert.Equal(SwitchSaveOutcome.Stale, (await SaveAsync(db, Change(false, false, false, 3, Noon.AddMinutes(3)))).Outcome);

            Assert.Equal(second.After, await db.Switches().GetSwitches(GuildId));
            Assert.Equal([second.After], await db.Switches().ListSwitches());
        }

        // Two forms drawn while the server had none, saved at the same moment: the first save's row waits for its commit, and
        // the second finds it there and is refused
        [Fact]
        public async Task FirstSavesOfTwoAtOnceOneWins()
        {
            var db = await StartAsync();
            var switches = db.Switches();
            var firstSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = switches.RunInTransaction(async transaction =>
            {
                var save = await switches.SaveSwitches(Change(false, true, true, 0, Noon), transaction);
                firstSaved.SetResult();
                await release.Task;
                return save;
            });

            try
            {
                await firstSaved.Task.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
                var second = SaveAsync(db, Change(true, true, false, 0, Noon.AddSeconds(1)));

                // The second save's locked read waits for the first one's new row
                Assert.Equal(1, await fixture.LockWaitsAsync("guild_settings", 1, WaitLimit));
                release.SetResult();

                var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                Assert.Equal([SwitchSaveOutcome.Saved, SwitchSaveOutcome.Stale], results.Select(result => result.Outcome));
                Assert.Equal(results[0].After, results[1].Before);
            }
            finally
            {
                release.TrySetResult();
            }

            var row = await switches.GetSwitches(GuildId);
            Assert.Equal((false, true, true, 1u), (row!.armor_on, row.maps_on, row.lobbies_on, row.version));
        }

        // Switches asked for that the server already has write nothing: no row for every kind on, and no new version or time
        // for the switches saved, whatever version the form had
        [Fact]
        public async Task UnchangedSaveWritesNothing()
        {
            var db = await StartAsync();

            Assert.Equal(new SwitchSave(SwitchSaveOutcome.Unchanged, null, null), await SaveAsync(db, Change(true, true, true, 0, Noon)));
            Assert.Null(await db.Switches().GetSwitches(GuildId));

            var saved = (await SaveAsync(db, Change(true, false, true, 0, Noon))).After;

            Assert.Equal(new SwitchSave(SwitchSaveOutcome.Unchanged, saved, null), await SaveAsync(db, Change(true, false, true, 1, Noon.AddHours(1))));
            Assert.Equal(SwitchSaveOutcome.Unchanged, (await SaveAsync(db, Change(true, false, true, 7, Noon.AddHours(1)))).Outcome);
            Assert.Equal(saved, await db.Switches().GetSwitches(GuildId));
        }
    }
}
