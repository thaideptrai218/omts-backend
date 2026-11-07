using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>The HarvestLots migration (PR H1a) on SQL Server: Up, Down to the schema it found, also over copies of a long lot code, Up again, the first-setup values on an existing cooperative. The demo lots and their rerun.</summary>
public class HarvestLotsMigrationAndDemoTests
{
    private const string MigrationBefore = "20261107050042_SeedingRecords";

    private const string SchemaQuery = """
        SELECT CONCAT('table ', t.name) AS [Value] FROM sys.tables t
        UNION ALL SELECT CONCAT('column ', OBJECT_NAME(c.object_id), ' ', c.name, ' ', c.precision, ' ', c.scale, ' ', c.is_nullable) FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
        UNION ALL SELECT CONCAT('index ', OBJECT_NAME(i.object_id), ' ', i.name, ' ', i.is_unique) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE i.type > 0
        UNION ALL SELECT CONCAT('fk ', f.name, ' ', f.delete_referential_action) FROM sys.foreign_keys f
        UNION ALL SELECT CONCAT('check ', c.name) FROM sys.check_constraints c
        """;

    private static async Task<List<string>> SchemaAsync(OmtsDbContext db) =>
        (await db.Database.SqlQueryRaw<string>(SchemaQuery).ToListAsync()).Order(StringComparer.Ordinal).ToList();

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<int> RecordCodeLengthAsync(OmtsDbContext db, string table, string column) =>
        ScalarAsync(db, $"SELECT CHARACTER_MAXIMUM_LENGTH AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'");

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_rows_around_them_and_end_with_no_pending_model_change()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var oldSchema = await SchemaAsync(db);
        Assert.DoesNotContain("table HarvestLots", oldSchema);

        // An existing cooperative on the row before this migration: no column of the settings exists yet.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES (NEWID(), N'HTX', SYSDATETIMEOFFSET())");

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(50, await ScalarAsync(db, "SELECT CAST(AverageKgPerCage AS int) AS [Value] FROM Cooperatives"));
        Assert.Equal(3, await ScalarAsync(db, "SELECT DaysBeforeAutoClose AS [Value] FROM Cooperatives"));
        var upSchema = await SchemaAsync(db);
        foreach (var expected in new[]
        {
            "table HarvestLots", "table HarvestLotLanes", "table HarvestLotPhotos",
            "index HarvestLots IX_HarvestLots_LotCode 1",
            "index HarvestLots IX_HarvestLots_RaftId_HarvestDate 0",
            "index HarvestLots IX_HarvestLots_CycleId 0",
            "index HarvestLots IX_HarvestLots_StatusId_HarvestDate 0",
            "index HarvestLotPhotos IX_HarvestLotPhotos_LotId_SortOrder 1",
            "fk FK_HarvestLots_CultivationCycles_CycleId 0",
            "fk FK_HarvestLots_Rafts_RaftId 0",
            "fk FK_HarvestLots_Accounts_RecordedByAccountId 0",
            "fk FK_HarvestLotLanes_HarvestLots_LotId 0",
            "fk FK_HarvestLotLanes_Lanes_LaneId 0",
            "fk FK_HarvestLotPhotos_HarvestLots_LotId 0",
            "check CK_HarvestLot_CageCount",
            "check CK_HarvestLot_EstimatedKg",
            "check CK_HarvestLot_KgPerCage",
            "check CK_HarvestLot_LeftoverKg",
            "check CK_HarvestLotPhoto_SortOrder",
            "check CK_Cooperative_AverageKgPerCage",
            "check CK_Cooperative_DaysBeforeAutoClose",
            "column HarvestLots EstimatedKg 18 3 0",
            "column HarvestLots KgPerCage 9 2 0",
            "column Cooperatives AverageKgPerCage 9 2 0",
            "index AuditEvents IX_AuditEvents_RecordType_OccurredAt 0",
            "index AuditEvents IX_AuditEvents_RecordCode_OccurredAt 0",
            "index Notifications IX_Notifications_RelatedRecordType_RelatedRecordId 0",
        })
        {
            Assert.Contains(expected, upSchema);
        }

        Assert.Equal(120, await RecordCodeLengthAsync(db, "AuditEvents", "RecordCode"));
        Assert.Equal(120, await RecordCodeLengthAsync(db, "Notifications", "RelatedRecordCode"));

        Assert.Equal(["CLOSED", "OPEN", "REOPENED"], await db.Statuses.Where(s => s.Group == StatusGroups.HarvestLot).Select(s => s.Code).OrderBy(c => c).ToListAsync());
        Assert.Equal([150, 151, 152], await db.StatusTransitions.Where(t => t.Id >= 150 && t.Id <= 152).Select(t => t.Id).OrderBy(i => i).ToListAsync());

        // Rows of a lot on both sides of the Down: it goes, and what it pointed to stays.
        var coop = await db.Cooperatives.FirstAsync();
        var zone = new FarmingZone { CooperativeId = coop.Id, Code = "KV01", Name = "A" };
        var lane = new Lane { LaneNumber = 1 };
        var cycle = new CultivationCycle { Raft = new Raft { Zone = zone, Code = "B01", Size = "1", Lanes = [lane] }, StartDate = new DateOnly(2026, 1, 1) };
        var account = new Account { Username = "owner", DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner, StatusId = StatusIds.Account.Active };
        db.AddRange(cycle, account);
        db.Add(new HarvestLot
        {
            LotCode = "KV01-B01-20260102", Raft = cycle.Raft, Cycle = cycle, HarvestedAt = DateTimeOffset.UtcNow, HarvestDate = new DateOnly(2026, 1, 2), CageCount = 1, KgPerCage = 50, EstimatedKg = 50,
            RecordedByAccountId = account.Id, Lanes = [new HarvestLotLane { Lane = lane, IsFinished = true }], Photos = [new HarvestLotPhoto { SortOrder = 1, FileKey = "harvest-lot-photos/k.png" }],
        });
        // Copies of the longest lot codes: they stay in the audit log and the lists, cut to what the old columns hold, and do not stop the Down.
        var longCode = new string('K', 120);
        db.AuditEvents.Add(new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "HarvestLot.Created", RecordType = "HarvestLot", RecordCode = longCode });
        db.Notifications.Add(new Notification { RecipientAccountId = account.Id, Type = "HARVEST_LOT_RECORDED", Title = "New harvest lot", RelatedRecordCode = longCode, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(oldSchema, await SchemaAsync(db));
        Assert.Equal(100, await RecordCodeLengthAsync(db, "AuditEvents", "RecordCode"));
        Assert.Equal(100, await RecordCodeLengthAsync(db, "Notifications", "RelatedRecordCode"));
        Assert.Equal(100, await ScalarAsync(db, "SELECT LEN(RecordCode) AS [Value] FROM AuditEvents WHERE RecordType = 'HarvestLot'"));
        Assert.Equal(100, await ScalarAsync(db, "SELECT LEN(RelatedRecordCode) AS [Value] FROM Notifications WHERE Type = 'HARVEST_LOT_RECORDED'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM CultivationCycles"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Lanes"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Statuses WHERE Id BETWEEN 2901 AND 2903"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM StatusTransitions WHERE Id BETWEEN 150 AND 152"));

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(upSchema, await SchemaAsync(db));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM HarvestLots"));
        Assert.Equal(50, await ScalarAsync(db, "SELECT CAST(AverageKgPerCage AS int) AS [Value] FROM Cooperatives"));
    }

    [SqlServerFact]
    public async Task The_demo_has_lots_by_the_managers_with_codes_states_notices_and_the_reminder_and_a_rerun_adds_nothing()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        try
        {
            await using var database = SqlServerTestDatabase.Create();
            await using var api = new SqlServerApiFactory(database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = storageRoot,
            });

            var first = await RunAsync(api);
            Assert.Equal(DeploymentBootstrapper.Success, first.ExitCode);
            Assert.Contains(first.Messages, m => m == "Demo harvest lots: 5 created.");

            var lots = await UseDbAsync(api, db => db.HarvestLots.AsNoTracking().OrderBy(l => l.LotCode)
                .Select(l => new { l.LotCode, Raft = l.Raft.Code, Zone = l.Raft.Zone.Code, l.StatusId, l.HarvestDate, l.KgPerCage, l.EstimatedKg, l.CageCount, l.RecordedByAccountId, Photos = l.Photos.Count, Finished = l.Lanes.Count(x => x.IsFinished) })
                .ToListAsync());
            Assert.Equal(["B1-01", "B1-01", "B1-02", "B2-01", "B2-01"], lots.Select(l => l.Raft).Order());
            var date = lots[0].HarvestDate.ToString("yyyyMMdd");
            var ofRaft1 = lots.Where(l => l.Raft == "B1-01").Select(l => l.LotCode).Order().ToList();
            Assert.Equal(ofRaft1[0] + "-2", ofRaft1[1]);
            Assert.All(lots, l =>
            {
                Assert.Contains($"-{date}", l.LotCode);
                Assert.StartsWith(l.Zone, l.LotCode);
                Assert.Equal(StatusIds.HarvestLot.Open, l.StatusId);
                Assert.Equal(50m, l.KgPerCage);
            });
            Assert.Contains(lots, l => l.EstimatedKg == 180m && l.CageCount == 4);
            Assert.Contains(lots, l => l.EstimatedKg == l.CageCount * 50m);
            Assert.Contains(lots, l => l.Raft == "B1-02" && l.Finished >= 2);
            Assert.Equal(3, lots.Sum(l => l.Photos));

            var managers = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "quanlyvung1" || a.Username == "quanlyvung2").Select(a => a.Id).ToListAsync());
            Assert.All(lots, l => Assert.Contains(l.RecordedByAccountId, managers));
            var owner = await UseDbAsync(api, db => db.Accounts.Where(a => a.Username == "owner").Select(a => a.Id).SingleAsync());
            Assert.Equal(5, await UseDbAsync(api, db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED" && n.RecipientAccountId == owner)));
            var reminded = await UseDbAsync(api, db => db.Notifications.Where(n => n.Type == "CYCLE_CLOSE_REMINDER").Select(n => n.RelatedRecordCode).ToListAsync());
            Assert.Equal(["B1-02"], reminded.Distinct());
            Assert.Equal(1, await UseDbAsync(api, db => db.CultivationCycles.CountAsync(c => c.CloseRemindedAt != null)));

            // The third raft of each zone keeps sessions that can still be corrected, and the fourth and fifth a cycle with no record.
            Assert.Equal(0, await UseDbAsync(api, db => db.HarvestLots.CountAsync(l => l.Raft.Code == "B1-03" || l.Raft.Code == "B2-03" || l.Raft.Code == "B1-04" || l.Raft.Code == "B2-05")));

            var rerun = await RunAsync(api);

            Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
            Assert.Contains(rerun.Messages, m => m == "Demo harvest lots: already there.");
            Assert.Equal(lots.Count, await UseDbAsync(api, db => db.HarvestLots.CountAsync()));
            Assert.Equal(5, await UseDbAsync(api, db => db.Notifications.CountAsync(n => n.Type == "HARVEST_LOT_RECORDED" && n.RecipientAccountId == owner)));

            // A lot gone from the table is not brought back: the audit log decides, as H1b's removal will leave it.
            var gone = await UseDbAsync(api, async db =>
            {
                var id = await db.HarvestLots.Where(l => l.Raft.Code == "B1-02").Select(l => l.Id).SingleAsync();
                await db.HarvestLotLanes.Where(l => l.LotId == id).ExecuteDeleteAsync();
                await db.HarvestLotPhotos.Where(p => p.LotId == id).ExecuteDeleteAsync();
                return await db.HarvestLots.Where(l => l.Id == id).ExecuteDeleteAsync();
            });
            Assert.Equal(1, gone);
            Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(api)).ExitCode);
            Assert.Equal(lots.Count - 1, await UseDbAsync(api, db => db.HarvestLots.CountAsync()));
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> api, Func<OmtsDbContext, Task<T>> query)
    {
        using var scope = api.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> api)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }
}
