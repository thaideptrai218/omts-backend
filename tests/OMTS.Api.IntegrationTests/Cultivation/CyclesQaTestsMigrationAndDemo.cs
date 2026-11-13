using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Constants;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Bootstrap;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>Independent QA of the CultivationCyclesV2 migration and of the demo cycle step on SQL Server.</summary>
public class CyclesQaTestsMigrationAndDemo
{
    private const string MigrationBefore = "20261105161142_RaftsV2";

    private const string SchemaQuery = """
        SELECT CONCAT('column ', c.name, ' ', t.name, ' ', c.max_length, ' ', c.precision, ' ', c.scale, ' ', c.is_nullable, ' ', c.default_object_id) AS [Value]
        FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('CultivationCycles')
        UNION ALL SELECT CONCAT('index ', i.name, ' ', i.is_unique, ' ', i.filter_definition) FROM sys.indexes i WHERE i.object_id = OBJECT_ID('CultivationCycles') AND i.type > 0
        UNION ALL SELECT CONCAT('fk ', f.name, ' ', f.delete_referential_action) FROM sys.foreign_keys f WHERE f.parent_object_id = OBJECT_ID('CultivationCycles')
        """;

    private static async Task<List<string>> SchemaAsync(OmtsDbContext db) =>
        (await db.Database.SqlQueryRaw<string>(SchemaQuery).ToListAsync()).Order(StringComparer.Ordinal).ToList();

    // UTC+7 at the very edges, other offsets, and a row that names a planned seed; Down gives back the schema it found, and Up runs again.
    [SqlServerFact]
    public async Task Up_moves_every_time_to_its_date_in_utc_plus_7_at_the_edge_of_midnight_and_down_restores_the_old_schema()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var oldSchema = await SchemaAsync(db);

        // Seed only columns of this historical schema; later migrations add fields to the current Cooperative model.
        var cooperativeId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({cooperativeId}, N'HTX', SYSDATETIMEOFFSET())");
        var zone = new FarmingZone { CooperativeId = cooperativeId, Code = "KV01", Name = "A" };
        var (raft, raftTwo) = (new Raft { Zone = zone, Code = "B01", Size = "1" }, new Raft { Zone = zone, Code = "B02", Size = "1" });
        var seed = new FarmingInputItem { Code = "VT001", Name = "Seed", Group = InputItemGroup.Seed, Unit = "con" };
        db.AddRange(raft, raftTwo, seed);
        await db.SaveChangesAsync();

        var (edge, offset, open, restartClosed, restartOpen) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO CultivationCycles (Id, RaftId, OpenedAt, ClosedAt, PlannedSeedTypeId, StatusId, CreatedAt)
            VALUES
                ({edge}, {raft.Id}, '2026-01-01T16:59:59.9999999+00:00', '2026-01-31T17:00:00+00:00', {seed.Id}, 602, SYSDATETIMEOFFSET()),
                ({offset}, {raft.Id}, '2026-03-01T23:59:59-05:00', '2026-03-31T23:30:00+07:00', NULL, 602, SYSDATETIMEOFFSET()),
                ({open}, {raft.Id}, '2026-04-01T17:00:00+00:00', NULL, {seed.Id}, 601, SYSDATETIMEOFFSET()),
                ({restartClosed}, {raftTwo.Id}, '2026-05-01T00:00:00+07:00', '2026-05-10T10:00:00+07:00', NULL, 602, SYSDATETIMEOFFSET()),
                ({restartOpen}, {raftTwo.Id}, '2026-05-10T11:00:00+07:00', NULL, NULL, 601, SYSDATETIMEOFFSET());
            """);

        await migrator.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        var cycles = await db.CultivationCycles.AsNoTracking().ToDictionaryAsync(c => c.Id);
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)), (cycles[edge].StartDate, cycles[edge].CloseDate));
        Assert.Equal((new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 31)), (cycles[offset].StartDate, cycles[offset].CloseDate));
        Assert.Equal((new DateOnly(2026, 4, 2), (DateOnly?)null), (cycles[open].StartDate, cycles[open].CloseDate));

        // Note N2, as it stands: a restart on the day of a close becomes two cycles sharing a day, which BR-CYCLE-01 forbids. Dev data only (PM 03/11).
        Assert.Equal(cycles[restartClosed].CloseDate, cycles[restartOpen].StartDate);

        await migrator.MigrateAsync(MigrationBefore);
        Assert.Equal(oldSchema, await SchemaAsync(db));

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        var again = await db.CultivationCycles.AsNoTracking().ToDictionaryAsync(c => c.Id);
        Assert.Equal((cycles[edge].StartDate, cycles[edge].CloseDate), (again[edge].StartDate, again[edge].CloseDate));
        Assert.Equal((cycles[offset].StartDate, cycles[offset].CloseDate), (again[offset].StartDate, again[offset].CloseDate));
        Assert.Equal((cycles[open].StartDate, cycles[open].CloseDate), (again[open].StartDate, again[open].CloseDate));
    }

    // Cancelled, or closed since: a rerun does not bring a demo cycle back, and adds no other row.
    [SqlServerFact]
    public async Task A_demo_rerun_does_not_bring_back_a_cancelled_cycle_and_changes_no_other()
    {
        await using var host = new DemoHost();
        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host.App)).ExitCode);

        var gone = await UseDbAsync(host.App, async db =>
        {
            var cycle = await db.CultivationCycles.OrderBy(c => c.StartDate).FirstAsync();
            db.CultivationCycles.Remove(cycle);
            await db.SaveChangesAsync();
            return cycle.Id;
        });
        var starts = await UseDbAsync(host.App, db => db.CultivationCycles.OrderBy(c => c.Id).Select(c => new { c.Id, c.StartDate, c.RowVersion }).ToListAsync());

        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host.App)).ExitCode);

        var after = await UseDbAsync(host.App, db => db.CultivationCycles.OrderBy(c => c.Id).Select(c => new { c.Id, c.StartDate, c.RowVersion }).ToListAsync());
        Assert.Equal(9, after.Count);
        Assert.DoesNotContain(after, c => c.Id == gone);
        Assert.Equal(starts.Select(c => (c.Id, c.StartDate, Convert.ToBase64String(c.RowVersion))), after.Select(c => (c.Id, c.StartDate, Convert.ToBase64String(c.RowVersion))));
    }

    // Finding L5. A demo database from before this PR has rafts but no cycle and no "Started" row. Like the other steps, this one leaves what exists as it is:
    // a raft the user discontinued gets no cycle, and the seed goes on to staff, receipts and water.
    [SqlServerFact]
    public async Task A_demo_rerun_on_an_older_database_skips_a_raft_the_user_discontinued()
    {
        await using var host = new DemoHost();
        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host.App)).ExitCode);
        var raftId = await UseDbAsync(host.App, async db =>
        {
            await db.CultivationLogPhotos.ExecuteDeleteAsync();
            await db.CultivationLogs.ExecuteDeleteAsync();
            await db.HarvestLotPhotos.ExecuteDeleteAsync();
            await db.HarvestLotLanes.ExecuteDeleteAsync();
            // The demo sales (PR 6a) point to the lots: an older database had none.
            await db.CustomerPayments.ExecuteDeleteAsync();
            await db.SalesInvoiceLines.ExecuteDeleteAsync();
            await db.SalesInvoices.ExecuteDeleteAsync();
            // Current demo sales also have port weighings; the simulated older database had neither.
            await db.Set<OMTS.Domain.Entities.Sales.PortWeighingCage>().ExecuteDeleteAsync();
            await db.Set<OMTS.Domain.Entities.Sales.PortWeighing>().ExecuteDeleteAsync();
            await db.HarvestLots.ExecuteDeleteAsync();
            await db.SeedingRecordLanes.ExecuteDeleteAsync();
            await db.SeedingRecords.ExecuteDeleteAsync();
            await db.CultivationCycles.ExecuteDeleteAsync();
            await db.AuditEvents.Where(e => e.Action == "CultivationCycle.Started").ExecuteDeleteAsync();
            var raft = await db.Rafts.Where(r => r.Code == "B1-03").SingleAsync();
            raft.StatusId = StatusIds.Usage.Discontinued;
            await db.SaveChangesAsync();
            return raft.Id;
        });

        var rerun = await RunAsync(host.App);

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        Assert.False(await UseDbAsync(host.App, db => db.CultivationCycles.AnyAsync(c => c.RaftId == raftId)));
    }

    // The same older database with its Open rows and no "Started" row: a start would be refused, so the rerun leaves them as they are.
    [SqlServerFact]
    public async Task A_demo_rerun_leaves_a_raft_with_a_cycle_and_no_audit_row_as_it_is()
    {
        await using var host = new DemoHost();
        Assert.Equal(DeploymentBootstrapper.Success, (await RunAsync(host.App)).ExitCode);
        await UseDbAsync(host.App, db => db.AuditEvents.Where(e => e.Action == "CultivationCycle.Started").ExecuteDeleteAsync());
        var before = await UseDbAsync(host.App, db => db.CultivationCycles.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync());

        var rerun = await RunAsync(host.App);

        Assert.Equal(DeploymentBootstrapper.Success, rerun.ExitCode);
        Assert.Equal(before, await UseDbAsync(host.App, db => db.CultivationCycles.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync()));
    }

    private static async Task<BootstrapOutcome> RunAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentBootstrapper>().RunAsync(true, CancellationToken.None);
    }

    private static async Task<T> UseDbAsync<T>(WebApplicationFactory<Program> app, Func<OmtsDbContext, Task<T>> use)
    {
        using var scope = app.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<OmtsDbContext>());
    }

    /// <summary>A fresh SQL Server database with the bootstrap settings of a demo deployment; dropped with its files.</summary>
    private sealed class DemoHost : IAsyncDisposable
    {
        private readonly SqlServerTestDatabase _database = SqlServerTestDatabase.Create();
        private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"omts-demo-files-{Guid.NewGuid():N}");
        private readonly SqlServerApiFactory _api;

        public DemoHost()
        {
            _api = new SqlServerApiFactory(_database, new Dictionary<string, string?>
            {
                [BootstrapSettings.CooperativeFullNameKey] = "HTX QA",
                [BootstrapSettings.OwnerDisplayNameKey] = "Owner QA",
                [BootstrapSettings.OwnerUsernameKey] = "owner",
                [BootstrapSettings.OwnerPhoneNumberKey] = "0900000001",
                [BootstrapSettings.OwnerTemporaryPasswordKey] = AcsTestKit.NewPassword(),
                ["Storage:Local:RootPath"] = _storageRoot,
            });
        }

        public WebApplicationFactory<Program> App => _api;

        public async ValueTask DisposeAsync()
        {
            await _api.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, recursive: true);
            }
        }
    }
}
