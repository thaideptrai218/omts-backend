using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// The ZonesLicensesV2 migration (PR 0.8) on SQL Server: a license that covered several zones keeps the one with the lowest
/// code (dev data only, no split), the scope table goes, and Down gives every license one scope row back.
/// </summary>
public class ZonesLicensesV2MigrationTests
{
    private const string MigrationBefore = "20261104174337_SalesCatalogueV2";

    [SqlServerFact]
    public async Task Up_keeps_one_zone_per_license_and_down_restores_one_scope_row_each()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);

        // Cooperatives and zones are unchanged by the migration, so the current model writes them.
        var cooperative = new Cooperative { FullName = "HTX" };
        var zoneB = new FarmingZone { Cooperative = cooperative, Code = "KV-B", Name = "B" };
        var zoneA = new FarmingZone { Cooperative = cooperative, Code = "KV-A", Name = "A" };
        db.FarmingZones.AddRange(zoneB, zoneA);
        await db.SaveChangesAsync();
        var (shared, single) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ZoneComplianceDocuments (Id, DocumentType, DocumentNumber, EffectiveDate, ExpiryDate, CreatedAt)
            VALUES ({shared}, N'AQUACULTURE_LICENSE', N'GP-1', '2025-01-01', '2027-01-01', SYSDATETIMEOFFSET()),
                   ({single}, N'OTHER', N'GP-2', '2025-01-01', '2027-01-01', SYSDATETIMEOFFSET());
            INSERT INTO DocumentZoneScopes (DocumentId, ZoneId) VALUES ({shared}, {zoneB.Id}), ({shared}, {zoneA.Id}), ({single}, {zoneB.Id});
            """);

        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        var zones = await db.ZoneComplianceDocuments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.ZoneId);
        Assert.Equal((zoneA.Id, zoneB.Id), (zones[shared], zones[single]));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'DocumentZoneScopes'"));
        // The type is optional now.
        var untyped = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO ZoneComplianceDocuments (Id, ZoneId, DocumentNumber, EffectiveDate, ExpiryDate, CreatedAt) VALUES ({untyped}, {zoneA.Id}, N'GP-3', '2025-01-01', '2027-01-01', SYSDATETIMEOFFSET())");

        await migrator.MigrateAsync(MigrationBefore);

        var scopes = await db.Database.SqlQuery<Guid>($"SELECT ZoneId AS [Value] FROM DocumentZoneScopes WHERE DocumentId = {shared}").ToListAsync();
        Assert.Equal([zoneA.Id], scopes);
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM DocumentZoneScopes WHERE ValidFrom = '2025-01-01' AND ValidTo = '2027-01-01'"));
        Assert.Equal("OTHER", await db.Database.SqlQuery<string>($"SELECT DocumentType AS [Value] FROM ZoneComplianceDocuments WHERE Id = {untyped}").SingleAsync());
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('ZoneComplianceDocuments') AND name = 'ZoneId'"));
    }

    /// <summary>A fixed query (no values) returning one number as <c>[Value]</c>.</summary>
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
