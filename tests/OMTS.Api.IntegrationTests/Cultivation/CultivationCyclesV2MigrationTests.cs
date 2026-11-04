using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// The CultivationCyclesV2 migration (PR C1) on SQL Server: Up turns the two times into business dates (UTC+7) and does not carry the
/// old seeding time or summary into the new reminder and assessment; Down gives the dates back as times; Up runs again on the result.
/// </summary>
public class CultivationCyclesV2MigrationTests
{
    private const string MigrationBefore = "20261104103030_RaftsV2";
    private const string TransitionGuard = "Every seeded lane is Harvested; any other close is an early close the Cooperative Owner approves first (BR-CYCLE-07).";

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_business_dates()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);

        var zone = new FarmingZone { CooperativeId = await OldSchemaRows.CooperativeAsync(db), Code = "KV01", Name = "A" };
        var (raft, raftTwo) = (new Raft { Zone = zone, Code = "B01", Size = "1" }, new Raft { Zone = zone, Code = "B02", Size = "1" });
        db.Rafts.AddRange(raft, raftTwo);
        await db.SaveChangesAsync();

        var (open, closed, noCloseTime) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        // 18:00 UTC is already the next day in UTC+7; 16:59 UTC is still the same day.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO CultivationCycles (Id, RaftId, OpenedAt, ClosedAt, SeedingCompletedAt, SeedingCompletionReason, ClosingSummary, StatusId, CreatedAt)
            VALUES
                ({closed}, {raft.Id}, '2026-01-01T18:00:00+00:00', '2026-02-10T16:59:00+00:00', '2026-01-05T00:00:00+00:00', N'done', N'a summary', 602, SYSDATETIMEOFFSET()),
                ({noCloseTime}, {raft.Id}, '2026-03-01T00:00:00+07:00', NULL, NULL, NULL, NULL, 602, SYSDATETIMEOFFSET()),
                ({open}, {raft.Id}, '2026-04-01T17:30:00+00:00', '2026-05-01T00:00:00+00:00', NULL, NULL, NULL, 601, SYSDATETIMEOFFSET());
            """);

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        await AssertUpAsync(db, open, closed, noCloseTime);
        Assert.Null(await db.CultivationCycles.AsNoTracking().Where(c => c.Id == closed).Select(c => c.CloseRemindedAt).SingleAsync());
        Assert.Null(await db.CultivationCycles.AsNoTracking().Where(c => c.Id == closed).Select(c => c.Assessment).SingleAsync());
        Assert.Null(await db.CultivationCycles.AsNoTracking().Where(c => c.Id == closed).Select(c => c.SeedingCompletedOn).SingleAsync());

        var transition = await db.StatusTransitions.AsNoTracking().SingleAsync(t => t.Id == 9);
        Assert.Equal(("CYCLE-06", TransitionGuard), (transition.UseCases, transition.GuardCondition));

        // Only the new model can hold these; Down does not keep them.
        await db.CultivationCycles.Where(c => c.Id == open).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpectedSeedQuantity, 5m).SetProperty(c => c.Assessment, "good"));

        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.FromHours(7)), await OpenedAtAsync(db, closed));
        Assert.Equal(new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.FromHours(7)), await ClosedAtAsync(db, closed));
        Assert.Null(await ClosedAtAsync(db, open));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = 'FK_CultivationCycles_FarmingInputItems_PlannedSeedTypeId'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('CultivationCycles') AND name IN ('StartDate', 'CloseDate', 'Assessment', 'ExpectedSeedQuantity')"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_CultivationCycles_RaftId_StartDate'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM StatusTransitions WHERE Id = 9 AND UseCases = N'UC27'"));
        await AssertOneOpenIndexAsync(db);

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        db.ChangeTracker.Clear();
        await AssertUpAsync(db, open, closed, noCloseTime);
        await AssertOneOpenIndexAsync(db);
        var defaults = await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('CultivationCycles') AND name = 'StartDate' AND default_object_id <> 0");
        Assert.Equal(0, defaults);

        db.CultivationCycles.Add(new CultivationCycle { RaftId = raftTwo.Id, StartDate = new DateOnly(2026, 6, 1) });
        await db.SaveChangesAsync();
        db.CultivationCycles.Add(new CultivationCycle { RaftId = raftTwo.Id, StartDate = new DateOnly(2026, 6, 2) });
        var second = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_CultivationCycles_RaftId_Open", second.InnerException!.Message);
    }

    private static async Task AssertUpAsync(OmtsDbContext db, Guid open, Guid closed, Guid noCloseTime)
    {
        var cycles = await db.CultivationCycles.AsNoTracking().ToDictionaryAsync(c => c.Id);
        Assert.Equal((new DateOnly(2026, 1, 2), new DateOnly(2026, 2, 10)), (cycles[closed].StartDate, cycles[closed].CloseDate));

        // A Closed row with no time closes on its start date, so no Closed cycle looks open for ever.
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1)), (cycles[noCloseTime].StartDate, cycles[noCloseTime].CloseDate));

        // An Open cycle has no close date, whatever its old row said.
        Assert.Equal((new DateOnly(2026, 4, 2), (DateOnly?)null, StatusIds.CultivationCycle.Open), (cycles[open].StartDate, cycles[open].CloseDate, cycles[open].StatusId));
        Assert.All(cycles.Values, c => Assert.False(c.ClosedEarly));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('CultivationCycles') AND name IN ('OpenedAt', 'ClosedAt', 'PlannedSeedTypeId', 'SeedingCompletedAt', 'SeedingCompletionReason', 'ClosingSummary')"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_CultivationCycles_RaftId_StartDate'"));
    }

    private static async Task AssertOneOpenIndexAsync(OmtsDbContext db) =>
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'UX_CultivationCycles_RaftId_Open' AND is_unique = 1 AND has_filter = 1 AND filter_definition = '([StatusId]=(601))'"));

    private static Task<DateTimeOffset?> OpenedAtAsync(OmtsDbContext db, Guid id) =>
        db.Database.SqlQuery<DateTimeOffset?>($"SELECT OpenedAt AS [Value] FROM CultivationCycles WHERE Id = {id}").SingleAsync();

    private static Task<DateTimeOffset?> ClosedAtAsync(OmtsDbContext db, Guid id) =>
        db.Database.SqlQuery<DateTimeOffset?>($"SELECT ClosedAt AS [Value] FROM CultivationCycles WHERE Id = {id}").SingleAsync();

    /// <summary>A fixed query (no values) returning one number as <c>[Value]</c>.</summary>
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
