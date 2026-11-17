using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Farming;

/// <summary>
/// The three migrations of 12/11/2026 on SQL Server (raft geometry with its backfill, lane incidents, harvest lot crew): Up, Down and Up again with the model equal to the last schema, and the backfill of
/// the old size text with rows that parse and rows that do not. Runs only with OMTS_TEST_SQLSERVER set, like the other migration tests.
/// </summary>
#pragma warning disable EF1002 // raw SQL below only interpolates constant table and column names of this test
public class RaftLanesMigrationSqlServerTests
{
    private const string Before = "20261111160000_SeedingSessionOverPlanReason";

    [SqlServerFact]
    public async Task Up_down_and_up_again_change_the_raft_lane_and_lot_tables_and_the_backfill_reads_only_the_form_the_web_wrote()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(Before);
        var (cooperative, zone) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync($"INSERT INTO Cooperatives (Id, FullName, CreatedAt) VALUES ({cooperative}, N'HTX', SYSDATETIMEOFFSET())");
        await db.Database.ExecuteSqlAsync($"INSERT INTO FarmingZones (Id, CooperativeId, Code, Name, StatusId, CreatedAt) VALUES ({zone}, {cooperative}, N'KV01', N'Zone', 101, SYSDATETIMEOFFSET())");
        var sizes = new (string Code, string? Size, decimal? Length, decimal? Width)[]
        {
            ("B01", "24 m x 10 m", 24m, 10m),
            ("B02", "10 m x 24 m", 10m, 24m),
            ("B03", "18.5 m x 9.25 m", 18.5m, 9.25m),
            ("B04", "40 x 20", null, null),
            ("B05", "big raft", null, null),
            ("B06", "24.555 m x 10 m", null, null),
            ("B07", "0 m x 10 m", null, null),
            ("B08", "501 m x 10 m", null, null),
            ("B09", "24 m x 201 m", null, null),
            ("B10", "1e2 m x 10 m", null, null),
            ("B11", " m x 10 m", null, null),
            ("B12", "24 m x  m", null, null),
            ("B13", null, null, null),
        };
        foreach (var (code, size, _, _) in sizes)
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO Rafts (Id, ZoneId, Code, Size, StatusId, CreatedAt) VALUES (NEWID(), {zone}, {code}, {size}, 101, SYSDATETIMEOFFSET())");
        }

        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var rows = await db.Rafts.AsNoTracking().ToDictionaryAsync(r => r.Code);
        foreach (var (code, size, length, width) in sizes)
        {
            Assert.True((length, width) == (rows[code].LengthM, rows[code].WidthM), $"{code} '{size}': read {rows[code].LengthM} x {rows[code].WidthM}");
            Assert.Equal(size, rows[code].Size); // the text is never touched
        }

        Assert.Equal(("decimal", 7, 2), await DecimalOfAsync(db, "Rafts", "LengthM"));
        Assert.Equal(1, await CountColumnsAsync(db, "Lanes", "LineLengthM"));
        Assert.Equal(2, await CountColumnsAsync(db, "Lanes", "IsDamaged", "OutOfServiceOn"));
        Assert.Equal(1, await CountTablesAsync(db, "LaneIncidents", "LaneIncidentPhotos", "LaneIncidentMaterials", "HarvestLotCrew") / 4);
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_Raft_Geometry'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_Lane_LineLength'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_LaneIncidentPhoto_SortOrder'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Lanes WHERE IsDamaged = 1 OR OutOfServiceOn IS NOT NULL"));

        await migrator.MigrateAsync(Before);
        Assert.Equal(0, await CountColumnsAsync(db, "Rafts", "LengthM", "WidthM"));
        Assert.Equal(0, await CountColumnsAsync(db, "Lanes", "LineLengthM", "IsDamaged", "OutOfServiceOn"));
        Assert.Equal(0, await CountTablesAsync(db, "LaneIncidents", "LaneIncidentPhotos", "LaneIncidentMaterials", "HarvestLotCrew"));
        Assert.Equal(sizes.Length, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Rafts"));
        Assert.Equal(12, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Rafts WHERE Size IS NOT NULL"));

        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM Rafts WHERE LengthM IS NOT NULL AND WidthM IS NOT NULL AND Code IN ('B01', 'B02')"));
    }

    [SqlServerFact]
    public async Task The_geometry_checks_refuse_a_length_or_width_out_of_range_in_the_database()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var cooperative = new OMTS.Domain.Entities.Identity.Cooperative { FullName = "HTX" };
        var zone = new OMTS.Domain.Entities.Farming.FarmingZone { Cooperative = cooperative, Code = "KV01", Name = "Zone" };
        db.FarmingZones.Add(zone);
        await db.SaveChangesAsync();

        foreach (var (length, width) in new[] { (501m, 10m), (24m, 0.5m) })
        {
            db.Rafts.Add(new OMTS.Domain.Entities.Farming.Raft { ZoneId = zone.Id, Code = $"X{length}", LengthM = length, WidthM = width });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
        }

        db.Rafts.Add(new OMTS.Domain.Entities.Farming.Raft { ZoneId = zone.Id, Code = "OK", LengthM = 24m, WidthM = 10m });
        await db.SaveChangesAsync();
    }

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<int> CountColumnsAsync(OmtsDbContext db, string table, params string[] columns)
    {
        var list = string.Join(", ", columns.Select(c => $"'{c}'"));
        return ScalarAsync(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name IN ({list})");
    }

    private static Task<int> CountTablesAsync(OmtsDbContext db, params string[] tables)
    {
        var list = string.Join(", ", tables.Select(t => $"'{t}'"));
        return ScalarAsync(db, $"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE name IN ({list})");
    }

    private static async Task<(string Type, int Precision, int Scale)> DecimalOfAsync(OmtsDbContext db, string table, string column) =>
        (await db.Database.SqlQueryRaw<string>($"SELECT t.name AS [Value] FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'").SingleAsync(),
            await ScalarAsync(db, $"SELECT CAST(c.precision AS int) AS [Value] FROM sys.columns c WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'"),
            await ScalarAsync(db, $"SELECT CAST(c.scale AS int) AS [Value] FROM sys.columns c WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'"));
}
