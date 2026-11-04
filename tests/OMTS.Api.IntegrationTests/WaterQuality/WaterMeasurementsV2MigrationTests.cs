using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>
/// The WaterMeasurementsV2 migration on SQL Server: up with old rows (drafts, a correction chain, voided), down and up
/// again. Old rows are dev and demo data and are removed; the schema and the status catalog match the model each way.
/// </summary>
public class WaterMeasurementsV2MigrationTests
{
    private const string NewWaterColumns = "N'RaftId', N'Temperature', N'TemperatureMinUsed', N'TemperatureMaxUsed'";

    private const string OldWaterColumns =
        "N'MeasurementPoint', N'InputSource', N'OcrPh', N'OcrSalinity', N'OcrMeasuredAt', N'ConfirmedByAccountId', N'ConfirmedAt', N'RecordVersion', N'CorrectedFromMeasurementId', N'CorrectionReason'";

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_schema_and_the_status_catalog_in_step_with_the_model()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var v2 = all.Single(m => m.EndsWith("_WaterMeasurementsV2", StringComparison.Ordinal));
        var before = all[all.IndexOf(v2) - 1];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(before);
        var (cooperative, zone, account) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, StatusId, CreatedAt) VALUES ({cooperative}, N'HTX', 201, SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt, WaterMeasurementPoint) VALUES ({zone}, {cooperative}, N'Z1', N'Zone 1', 101, SYSDATETIMEOFFSET(), N'Phao 1')");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt) VALUES ({account}, N'old', N'Old', N'x', N'Staff', 301, 0, 0, SYSDATETIMEOFFSET())");
        // A draft, a corrected version pointing at its successor's predecessor (the self reference), its successor and a voided row.
        var (draft, corrected, successor, voided) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await InsertOldRowAsync(db, draft, zone, account, 701, null);
        await InsertOldRowAsync(db, corrected, zone, account, 703, null);
        await InsertOldRowAsync(db, successor, zone, account, 702, corrected);
        await InsertOldRowAsync(db, voided, zone, account, 704, null);
        Assert.Equal(4, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [WaterMeasurements]"));

        await db.Database.MigrateAsync();

        await AssertV2Async(db);
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [WaterMeasurements]"));

        await migrator.MigrateAsync(before);

        Assert.Equal(0, await ScalarAsync<int>(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name IN ({NewWaterColumns})"));
        Assert.Equal(10, await ScalarAsync<int>(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name IN ({OldWaterColumns})"));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'FarmingZones') AND name = N'WaterMeasurementPoint'"));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name = N'Ph' AND is_nullable = 1"));
        Assert.Equal(4, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [Statuses] WHERE [Group] = N'WATER_MEASUREMENT'"));
        Assert.Equal(4, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [StatusTransitions] WHERE [Id] IN (10, 11, 55, 56)"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [Statuses] WHERE [Id] IN (2101, 2102)"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [StatusTransitions] WHERE [Id] = 80"));

        await db.Database.MigrateAsync();

        await AssertV2Async(db);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task AssertV2Async(OmtsDbContext db)
    {
        Assert.Equal(4, await ScalarAsync<int>(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name IN ({NewWaterColumns})"));
        Assert.Equal(0, await ScalarAsync<int>(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name IN ({OldWaterColumns})"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'FarmingZones') AND name = N'WaterMeasurementPoint'"));
        Assert.Equal(2, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'FarmingZones') AND name IN (N'TemperatureMin', N'TemperatureMax')"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name IN (N'Ph', N'Salinity', N'Temperature', N'PhotoUrl', N'RaftId') AND is_nullable = 1"));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'WaterMeasurements') AND name = N'IX_WaterMeasurements_PhotoUrl' AND is_unique = 1"));
        Assert.Equal(1, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = N'FK_WaterMeasurements_Rafts_RaftId'"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [Statuses] WHERE [Id] BETWEEN 701 AND 704 OR [Group] = N'WATER_MEASUREMENT'"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [StatusTransitions] WHERE [Id] IN (10, 11, 55, 56)"));
        var statuses = await db.Statuses.AsNoTracking().Where(s => s.Group == StatusGroups.WaterMeasurement).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(
            [(StatusIds.WaterMeasurement.Active, "ACTIVE", true, false), (StatusIds.WaterMeasurement.Voided, "VOIDED", false, true)],
            statuses.Select(s => (s.Id, s.Code, s.IsInitial, s.IsFinal)));
        var transition = await db.StatusTransitions.AsNoTracking().SingleAsync(t => t.Id == 80);
        Assert.Equal((StatusIds.WaterMeasurement.Active, StatusIds.WaterMeasurement.Voided, true), (transition.FromStatusId, transition.ToStatusId, transition.ReasonRequired));
    }

    private static Task InsertOldRowAsync(OmtsDbContext db, Guid id, Guid zone, Guid account, int status, Guid? correctedFrom) =>
        db.Database.ExecuteSqlAsync(
            $"INSERT INTO WaterMeasurements (Id, ZoneId, MeasuredAt, Ph, Salinity, InputSource, ClientOperationId, MeasuredByAccountId, ThresholdExceeded, RecordVersion, StatusId, CorrectedFromMeasurementId, CreatedAt) VALUES ({id}, {zone}, SYSDATETIMEOFFSET(), 7.5, 20, N'Manual', {Guid.NewGuid()}, {account}, 0, 1, {status}, {correctedFrom}, SYSDATETIMEOFFSET())");

    // Only constant SQL of this test class: no value from outside is concatenated.
    private static Task<T> ScalarAsync<T>(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<T>(sql).SingleAsync();
}
