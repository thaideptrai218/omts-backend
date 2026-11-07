using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Platform;
using OMTS.Infrastructure.Persistence;

namespace OMTS.Api.IntegrationTests.Harvest;

/// <summary>QA of PR H1b: the migration on the data a database of H1a holds, and on the one it cannot take.</summary>
public class HarvestCorrectionsQaTestsMigration
{
    private const string MigrationBefore = "20261107052218_HarvestLots";
    private const string IndexName = "UX_AuditEvents_HarvestLotCreatedCode";

    private static AuditEvent Created(string? code) => new() { OccurredAt = DateTimeOffset.UtcNow, Action = "HarvestLot.Created", RecordType = "HarvestLot", RecordCode = code };

    private static Task<int> IndexCountAsync(OmtsDbContext db) =>
        db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = '{IndexName}'").SingleAsync();

    // What a database of H1a holds: one Created row per lot, codes that differ only by a suffix, the longest code, and rows of other actions and types.
    [SqlServerFact]
    public async Task The_up_passes_on_what_a_database_of_h1a_holds_and_no_row_changes()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var longest = new string('K', 118);
        db.AuditEvents.AddRange(
            Created("KV01-B01-20261026"), Created("KV01-B01-20261026-2"), Created("KV01-B01-20261027"), Created($"{longest}-2"), Created(null), Created(null),
            new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "Raft.Created", RecordType = "Raft", RecordCode = "KV01-B01-20261026" },
            new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "HarvestLot.Closed", RecordType = "HarvestLot", RecordCode = "KV01-B01-20261026" },
            new AuditEvent { OccurredAt = DateTimeOffset.UtcNow, Action = "HarvestLot.Closed", RecordType = "HarvestLot", RecordCode = "KV01-B01-20261026" });
        await db.SaveChangesAsync();
        var before = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Id, e.Action, e.RecordCode }).ToListAsync();

        await migrator.MigrateAsync();

        Assert.Equal(1, await IndexCountAsync(db));
        Assert.Equal(before, await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id).Select(e => new { e.Id, e.Action, e.RecordCode }).ToListAsync());
    }

    // The note of the migration: two Created rows of one code stop it. It fails whole, says which index, and a clean-up and a second try pass. Up, Down and Up with real rows are in the branch's test.
    [SqlServerFact]
    public async Task On_two_created_rows_of_one_code_the_up_stops_leaves_no_index_and_passes_once_they_are_gone()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(MigrationBefore);
        var extra = Created("kv01-b01-20261026");
        db.AuditEvents.AddRange(Created("KV01-B01-20261026"), extra);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var failure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync());

        Assert.Equal(1505, failure.Number);
        Assert.Contains(IndexName, failure.Message);
        Assert.Equal(0, await IndexCountAsync(db));
        Assert.Contains("20261107055301_HarvestLotCorrections", await db.Database.GetPendingMigrationsAsync());

        await db.Database.ExecuteSqlRawAsync("DELETE FROM AuditEvents WHERE Id = {0}", extra.Id);
        await migrator.MigrateAsync();
        Assert.Equal(1, await IndexCountAsync(db));
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
