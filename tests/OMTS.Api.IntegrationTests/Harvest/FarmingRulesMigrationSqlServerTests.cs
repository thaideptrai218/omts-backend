using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>
/// The two migrations of 11/11/2026 on SQL Server (cages as decimal(9,2) with the over-plan reason, and the stock issue links): Up, Down and Up again, with the model equal to the last schema.
/// Runs only with OMTS_TEST_SQLSERVER set, like the other migration tests.
/// </summary>
#pragma warning disable EF1002 // raw SQL below only interpolates constant table and column names of this test
public class FarmingRulesMigrationSqlServerTests
{
    private const string Before = "20261111135229_CageOrdersWorkPlansHarvestAndWeighing";

    [SqlServerFact]
    public async Task Up_down_and_up_again_change_the_cage_column_and_the_issue_links()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(Before);
        Assert.Equal("int", await TypeOfAsync(db, "HarvestLots", "CageCount"));
        Assert.Equal(0, await CountColumnsAsync(db, "HarvestLots", "OverPlanReason"));

        await migrator.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(("decimal", 9, 2), await DecimalOfAsync(db, "HarvestLots", "CageCount"));
        Assert.Equal(1, await CountColumnsAsync(db, "HarvestLots", "OverPlanReason"));
        Assert.Equal(1, await CountColumnsAsync(db, "SeedingRecords", "IssueId"));
        Assert.Equal(1, await CountColumnsAsync(db, "MaterialUsages", "IssueId"));
        Assert.Equal(3, await CountColumnsAsync(db, "InputIssues", "SourceKind", "SourceRecordId", "CycleId") );
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_HarvestLot_CageCount'"));

        await migrator.MigrateAsync(Before);
        Assert.Equal("int", await TypeOfAsync(db, "HarvestLots", "CageCount"));
        Assert.Equal(0, await CountColumnsAsync(db, "HarvestLots", "OverPlanReason"));
        Assert.Equal(0, await CountColumnsAsync(db, "SeedingRecords", "IssueId"));
        Assert.Equal(0, await CountColumnsAsync(db, "InputIssues", "SourceKind"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM sys.check_constraints WHERE name = 'CK_HarvestLot_CageCount'"));

        await migrator.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); // the model was compared once above: EF cannot compare a second time on one context
        Assert.Equal(("decimal", 9, 2), await DecimalOfAsync(db, "HarvestLots", "CageCount"));
    }

    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private static Task<int> CountColumnsAsync(OmtsDbContext db, string table, params string[] columns)
    {
        var list = string.Join(", ", columns.Select(c => $"'{c}'"));
        return ScalarAsync(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('{table}') AND name IN ({list})");
    }

    private static async Task<string> TypeOfAsync(OmtsDbContext db, string table, string column) =>
        await db.Database.SqlQueryRaw<string>($"SELECT t.name AS [Value] FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'").SingleAsync();

    private static async Task<(string Type, int Precision, int Scale)> DecimalOfAsync(OmtsDbContext db, string table, string column) =>
        (await TypeOfAsync(db, table, column),
            await ScalarAsync(db, $"SELECT CAST(c.precision AS int) AS [Value] FROM sys.columns c WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'"),
            await ScalarAsync(db, $"SELECT CAST(c.scale AS int) AS [Value] FROM sys.columns c WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'"));
}
