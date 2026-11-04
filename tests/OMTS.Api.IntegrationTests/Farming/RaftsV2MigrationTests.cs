using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// The RaftsV2 migration (PR 0.9) on SQL Server: Up moves PhotoUrl to the first RaftPhoto, makes every lane In Use and drops
/// the layout versions, segments and log entries, even when rows point to them; Down keeps the first photo and deletes
/// Removed lanes; Up runs again on the result.
/// </summary>
public class RaftsV2MigrationTests
{
    private const string MigrationBefore = "20261104072437_ZonesLicensesV2";
    private const string OldPhoto = "raft-photos/2026/01/old.jpg";
    private const string SecondPhoto = "raft-photos/2026/01/second.jpg";

    private static readonly string[] DroppedTables = ["RaftLayoutVersions", "LaneSegments", "CultivationLogEntries"];

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_first_photo_and_the_lanes_in_use()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);

        // Cooperatives, zones and accounts are unchanged by the migration, so the current model writes them.
        var account = new Account { Username = NewCode(), DisplayName = "Owner", PasswordHash = NewPassword(), SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        var zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX" }, Code = "KV01", Name = "A" };
        db.Accounts.Add(account);
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();
        var (withPhoto, blankPhoto, cycle, lane, entry) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO Rafts (Id, ZoneId, Code, PhotoUrl, StatusId, CreatedAt)
            VALUES ({withPhoto}, {zone.Id}, N'B01', {OldPhoto}, 101, SYSDATETIMEOFFSET()), ({blankPhoto}, {zone.Id}, N'B02', N'', 101, SYSDATETIMEOFFSET());
            INSERT INTO Lanes (Id, RaftId, LaneNumber, Length, MeasurementMarkers, CreatedAt)
            VALUES ({lane}, {withPhoto}, 1, 40, N'markers', SYSDATETIMEOFFSET()), (NEWID(), {withPhoto}, 2, 40, NULL, SYSDATETIMEOFFSET()), (NEWID(), {withPhoto}, 3, NULL, NULL, SYSDATETIMEOFFSET());
            INSERT INTO RaftLayoutVersions (Id, RaftId, VersionNumber, LaneCount, ValidFrom, CreatedAt)
            VALUES (NEWID(), {withPhoto}, 1, 3, '2026-01-01', SYSDATETIMEOFFSET());
            INSERT INTO CultivationCycles (Id, RaftId, OpenedAt, ClosedAt, StatusId, CreatedAt)
            VALUES ({cycle}, {withPhoto}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 602, SYSDATETIMEOFFSET());
            INSERT INTO CultivationLogEntries (Id, ZoneId, CycleId, LaneId, OccurredAt, EventType, RecordedByAccountId, EvidenceUrls, CreatedAt)
            VALUES ({entry}, {zone.Id}, {cycle}, {lane}, SYSDATETIMEOFFSET(), N'Observation', {account.Id}, N'[]', SYSDATETIMEOFFSET());
            INSERT INTO LaneSegments (Id, CycleId, LaneId, LogId, SegmentType, FromPosition, ToPosition, SegmentDate, RecordedByAccountId, CreatedAt)
            VALUES (NEWID(), {cycle}, {lane}, {entry}, N'WrittenOff', 0, 10, '2026-01-01', {account.Id}, SYSDATETIMEOFFSET());
            """);

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await AssertUpAsync(db, withPhoto, blankPhoto);
        Assert.Equal(["IN_USE", "REMOVED"], await db.Statuses.AsNoTracking().Where(s => s.Group == StatusGroups.Lane).OrderBy(s => s.Id).Select(s => s.Code).ToListAsync());
        Assert.Equal(2, await db.StatusTransitions.AsNoTracking().CountAsync(t => t.FromStatusId == StatusIds.Lane.InUse || t.FromStatusId == StatusIds.Lane.Removed));

        // What only the new model can hold: a second photo and a Removed lane.
        db.RaftPhotos.Add(new RaftPhoto { RaftId = withPhoto, SortOrder = 2, FileKey = SecondPhoto });
        await db.SaveChangesAsync();
        await db.Lanes.Where(l => l.RaftId == withPhoto && l.LaneNumber == 3).ExecuteUpdateAsync(s => s.SetProperty(l => l.StatusId, StatusIds.Lane.Removed));

        await migrator.MigrateAsync(MigrationBefore);

        Assert.Equal(OldPhoto, await db.Database.SqlQuery<string>($"SELECT PhotoUrl AS [Value] FROM Rafts WHERE Id = {withPhoto}").SingleAsync());
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Rafts WHERE PhotoUrl IS NULL"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Lanes"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'RaftPhotos'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id IN (2001, 2002)"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Lanes') AND name = 'StatusId'"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Lanes') AND name IN ('Length', 'MeasurementMarkers')"));
        Assert.Empty(DroppedTables.Except(await TablesAsync(db)));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM RaftLayoutVersions"));

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        db.ChangeTracker.Clear();
        Assert.Equal([1, 2], await db.Lanes.AsNoTracking().Where(l => l.RaftId == withPhoto).OrderBy(l => l.LaneNumber).Select(l => l.LaneNumber).ToListAsync());
        await AssertUpAsync(db, withPhoto, blankPhoto);
    }

    private static async Task AssertUpAsync(OmtsDbContext db, Guid withPhoto, Guid blankPhoto)
    {
        var photo = await db.RaftPhotos.AsNoTracking().SingleAsync();
        Assert.Equal((withPhoto, 1, OldPhoto), (photo.RaftId, photo.SortOrder, photo.FileKey));
        Assert.False(await db.RaftPhotos.AsNoTracking().AnyAsync(p => p.RaftId == blankPhoto));
        Assert.All(await db.Lanes.AsNoTracking().ToListAsync(), l => Assert.Equal(StatusIds.Lane.InUse, l.StatusId));
        Assert.Empty(DroppedTables.Intersect(await TablesAsync(db)));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Rafts') AND name = 'PhotoUrl'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('Lanes') AND name IN ('Length', 'MeasurementMarkers')"));
    }

    private static async Task<List<string>> TablesAsync(OmtsDbContext db) =>
        await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();

    /// <summary>A fixed query (no values) returning one number as <c>[Value]</c>.</summary>
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
