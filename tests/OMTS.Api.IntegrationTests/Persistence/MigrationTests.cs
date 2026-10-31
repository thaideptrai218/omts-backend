using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Domain.Entities.Platform;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Persistence;

public class MigrationTests
{
    // Tables of the SRS 22/10 order, harvest, traceability, claim, finance and pay model (OMTS-216).
    private static readonly string[] LegacyTables =
    [
        "SalesOrders", "SaleTransactions", "PortWeighings", "PortHandovers",
        "CustomerReceipts", "ReceiptAllocations", "CustomerCredits", "CustomerRefunds",
        "QualityClaims", "PhysicalReturns", "SaleAdjustments", "MessageDeliveryAttempts",
        "HarvestEvents", "TraceabilityLots", "TracePublicationVersions", "TraceCorrections",
        "PayRuleVersions", "PayLines", "LaborSettlements", "LaborPayments",
    ];

    // Still read by live endpoints (raft lanes in use, end of employment) or replaced by later tickets.
    private static readonly string[] KeptTables =
    [
        "CultivationCycles", "SeedingEvents", "SeedReceiptLots", "LaneSegments", "CultivationLogEntries",
        "Products", "Customers", "Contractors", "SeasonalWorkers", "WorkAssignments", "AttendanceRecords",
    ];

    private const string MigrationBeforeCleanup = "20261025175348_AddPushDeviceLoginSession";

    [Fact]
    public void Integration_snapshot_matches_the_current_model()
    {
        using var db = new OmtsDbContext(new DbContextOptionsBuilder<OmtsDbContext>()
            .UseSqlServer("Server=unused;Database=model_check;Integrated Security=true;TrustServerCertificate=true").Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [SqlServerFact]
    public async Task Cleanup_refuses_existing_payroll_locks_and_keeps_their_rows()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeCleanup);
        // This isolated legacy fixture needs no account: disable only its FK checks, never real QA data.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE [LaborSettlements] NOCHECK CONSTRAINT ALL;
            INSERT INTO [LaborSettlements]
                ([Id], [PeriodStart], [PeriodEnd], [TotalObligation], [PreparedByAccountId], [StatusId], [CreatedAt])
            VALUES (NEWID(), '2026-10-01', '2026-10-31', 1000, NEWID(), 1502, SYSUTCDATETIME());
            """);
        var repository = new OMTS.Infrastructure.Persistence.Repositories.Workforce.WorkforceRepository(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.HasLockedPayrollFromAsync(new DateOnly(2026, 10, 1), default));
        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.MigrateAsync());
        Assert.Equal(51001, error.Number);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [LaborSettlements]").SingleAsync());
        Assert.Contains("20261028151320_RemoveLegacyScaffolding", await db.Database.GetPendingMigrationsAsync());
    }

    // OMTS-208 done criteria: a fresh database built from the migrations has the status catalog, keeps the
    // schema rules (no cascade delete) and the API starts on it.
    [SqlServerFact]
    public async Task Fresh_database_from_migrations_has_the_status_catalog_and_serves_the_api()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Status))!.GetSeedData().Count(), await db.Statuses.CountAsync());
            Assert.Equal(db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StatusTransition))!.GetSeedData().Count(), await db.StatusTransitions.CountAsync());
            var cascadingKeys = await db.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE delete_referential_action <> 0")
                .SingleAsync();
            Assert.Equal(0, cascadingKeys);
        }

        // Added last, so it wins over a developer's user-secrets.
        await using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:OmtsDb"] = database.ConnectionString, ["Jobs:OrphanFileCleanup:Enabled"] = "false" })));
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { identifier = "nobody", password = "Secret123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // OMTS-216: one migration drops the legacy tables, their LaneSegments columns and status rows, keeps
    // the tables live code still reads, and reverts cleanly (Down) and re-applies.
    [SqlServerFact]
    public async Task Legacy_scaffolding_is_dropped_and_the_cleanup_migration_reverts()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();

        await db.Database.MigrateAsync();
        var tables = await TableNamesAsync(db);

        Assert.Empty(LegacyTables.Intersect(tables));
        Assert.Empty(KeptTables.Except(tables));
        Assert.Empty(new[] { "OrderId", "HarvestEventId", "LotId" }.Intersect(await ColumnNamesAsync(db, "LaneSegments")));
        Assert.False(await db.Statuses.AnyAsync(s => s.Id >= 801 && s.Id <= 1603));
        Assert.False(await db.StatusTransitions.AnyAsync(t => t.Id >= 12 && t.Id <= 54));

        await migrator.MigrateAsync(MigrationBeforeCleanup);
        Assert.Empty(LegacyTables.Except(await TableNamesAsync(db)));
        Assert.Equal(38, await db.Statuses.CountAsync(s => s.Id >= 801 && s.Id <= 1603));
        Assert.Equal(43, await db.StatusTransitions.CountAsync(t => t.Id >= 12 && t.Id <= 54));

        await db.Database.MigrateAsync();
        Assert.Empty(LegacyTables.Intersect(await TableNamesAsync(db)));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    private static async Task<List<string>> TableNamesAsync(OmtsDbContext db) =>
        await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables").ToListAsync();

    private static async Task<List<string>> ColumnNamesAsync(OmtsDbContext db, string table) =>
        await db.Database.SqlQuery<string>($"SELECT name AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID({table})").ToListAsync();
}
