using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// QA of the ZonesLicensesV2 migration on SQL Server: Up, Down and Up again keep the data of a license (its markers and its
/// replacement link), the lowest code ignores case, the model has no pending change, and a license with no zone at all
/// stops the migration and leaves the database as it was.
/// </summary>
public class ZonesLicensesMigrationQaTests
{
    private static string MigrationBefore(DbContext db)
    {
        var migrations = db.Database.GetMigrations().ToList();
        return migrations[migrations.FindIndex(m => m.EndsWith("_ZonesLicensesV2", StringComparison.Ordinal)) - 1];
    }

    [SqlServerFact]
    public async Task Up_down_and_up_again_keep_the_license_data_and_the_lowest_code_ignores_case()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore(db));

        var cooperative = new Cooperative { FullName = "HTX" };
        var upper = new FarmingZone { Cooperative = cooperative, Code = "KV-B", Name = "B" };
        var lower = new FarmingZone { Cooperative = cooperative, Code = "kv-a", Name = "A" };
        db.FarmingZones.AddRange(upper, lower);
        await db.SaveChangesAsync();
        var (old, renewal) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ZoneComplianceDocuments (Id, DocumentType, DocumentNumber, EffectiveDate, ExpiryDate, ExpiringReminderSentAt, CreatedAt)
            VALUES ({old}, N'OTHER', N'GP-1', '2025-01-01', '2027-01-01', '2026-10-01T00:00:00+00:00', SYSDATETIMEOFFSET());
            INSERT INTO ZoneComplianceDocuments (Id, DocumentType, DocumentNumber, EffectiveDate, ExpiryDate, ReplacesDocumentId, CreatedAt)
            VALUES ({renewal}, N'AQUACULTURE_LICENSE', N'GP-2', '2026-01-01', '2028-01-01', {old}, SYSDATETIMEOFFSET());
            INSERT INTO DocumentZoneScopes (DocumentId, ZoneId) VALUES ({old}, {upper.Id}), ({old}, {lower.Id}), ({renewal}, {upper.Id});
            """);

        await migrator.MigrateAsync();
        await AssertLicensesAsync();
        Assert.False(db.Database.HasPendingModelChanges());

        await migrator.MigrateAsync(MigrationBefore(db));
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM DocumentZoneScopes").SingleAsync());
        await migrator.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await AssertLicensesAsync();

        async Task AssertLicensesAsync()
        {
            var licenses = await db.ZoneComplianceDocuments.AsNoTracking().ToDictionaryAsync(d => d.Id);
            Assert.Equal((lower.Id, upper.Id), (licenses[old].ZoneId, licenses[renewal].ZoneId));
            Assert.Equal(old, licenses[renewal].ReplacesDocumentId);
            Assert.NotNull(licenses[old].ExpiringReminderSentAt);
            Assert.Equal("OTHER", licenses[old].DocumentType);
        }
    }

    // The old API always kept a zone on a license, so only hand-made data has none: the deploy stops without a half-done schema.
    [SqlServerFact]
    public async Task A_license_with_no_zone_stops_the_migration_and_leaves_the_database_unchanged()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore(db));
        var orphan = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO ZoneComplianceDocuments (Id, DocumentType, DocumentNumber, EffectiveDate, ExpiryDate, CreatedAt)
            VALUES ({orphan}, N'OTHER', N'GP-0', '2025-01-01', '2027-01-01', SYSDATETIMEOFFSET())
            """);

        await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync());

        Assert.Equal(MigrationBefore(db), (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name = 'DocumentZoneScopes'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('ZoneComplianceDocuments') AND name = 'ZoneId'").SingleAsync());
    }
}
