using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>QA of the water model on SQL Server: parallel requests never answer 500, and the migration goes down to exactly what it came from.</summary>
public class WaterModelQaSqlServerTests
{
    // Down with rows of the new model, then a comparison with the schema and the catalog before the migration.
    [SqlServerFact]
    public async Task Down_with_new_rows_restores_the_schema_and_catalog_exactly_and_up_works_again()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var v2 = all.Single(m => m.EndsWith("_WaterMeasurementsV2", StringComparison.Ordinal));
        var before = all[all.IndexOf(v2) - 1];
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(before);
        var original = await ShapeAsync(db);

        var seeded = await WaterSqlServerKit.SeedAsync(database);
        var account = new Account { Username = NewCode(), DisplayName = "QA", PasswordHash = NewPassword(), StatusId = StatusIds.Account.Active };
        db.Accounts.Add(account);
        db.WaterMeasurements.Add(new WaterMeasurement
        {
            ZoneId = seeded.Zone.Id,
            RaftId = seeded.Raft.Id,
            MeasuredAt = DateTimeOffset.UtcNow,
            Ph = 7m,
            Salinity = 20m,
            Temperature = 25m,
            PhotoUrl = $"water-meter-photos/{Guid.NewGuid():N}/2026/11/{Guid.NewGuid():N}.png",
            ClientOperationId = Guid.NewGuid(),
            MeasuredByAccountId = account.Id,
        });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(before);

        Assert.Equal(original, await ShapeAsync(db));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM [WaterMeasurements]"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS [Value] FROM [Rafts]"));

        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SqlServerFact]
    public async Task Parallel_records_with_one_operation_and_different_photos_save_one_record_and_never_answer_500()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, await OwnerAsync(api));
        var operation = Guid.NewGuid();
        var bodies = new List<object>();
        for (var i = 0; i < 6; i++)
        {
            bodies.Add(new
            {
                clientOperationId = operation,
                raftId = seeded.Raft.Id,
                measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ph = 7m,
                salinity = 20m,
                temperature = 25m,
                photoKey = await client.PhotoKeyAsync(seeded.Zone.Id),
            });
        }

        var responses = await Task.WhenAll(bodies.Select(b => client.PostAsJsonAsync(Base(seeded), b)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"{(int)r.StatusCode}"));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.WaterMeasurements.CountAsync());
    }

    [SqlServerFact]
    public async Task Parallel_voids_let_one_win_and_the_others_answer_a_conflict_not_a_500()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, await OwnerAsync(api));
        var id = await seeded.RecordAsync(client, 7m, 20m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.VoidAsync(id)));

        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)r.StatusCode}"));
        await using var db = database.CreateContext();
        Assert.Equal(responses.Count(r => r.StatusCode == HttpStatusCode.OK), await db.AuditEvents.CountAsync(e => e.RecordId == id && e.Action == "WaterMeasurement.Voided"));
    }

    // Records and threshold changes side by side: no 500, and each copy is one whole set of thresholds, never a mix.
    [SqlServerFact]
    public async Task Parallel_records_and_threshold_changes_never_answer_500_and_each_copy_is_one_whole_set()
    {
        await using var database = SqlServerTestDatabase.Create();
        var seeded = await WaterSqlServerKit.SeedAsync(database);
        await using var api = new SqlServerApiFactory(database);
        var client = await LoginAsync(api, await OwnerAsync(api));
        var keys = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            keys.Add(await client.PhotoKeyAsync(seeded.Zone.Id));
        }

        var records = keys.Select(key => client.PostAsJsonAsync(Base(seeded), new
        {
            raftId = seeded.Raft.Id,
            measuredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ph = 7m,
            salinity = 20m,
            temperature = 25m,
            photoKey = key,
        }));
        var changes = Enumerable.Range(1, 6).Select(i => client.PutAsJsonAsync($"/api/zones/{seeded.Zone.Id}/water-thresholds", new
        {
            phMin = 1m * i,
            phMax = 1m * i + 1,
            salinityMin = 1m * i,
            salinityMax = 1m * i + 1,
            temperatureMin = 1m * i,
            temperatureMax = 1m * i + 1,
        }));

        var responses = await Task.WhenAll(records.Concat(changes));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict, $"{(int)r.StatusCode}"));
        await using var db = database.CreateContext();
        var saved = await db.WaterMeasurements.AsNoTracking().ToListAsync();
        Assert.Equal(6, saved.Count);
        Assert.All(saved, m =>
        {
            if (m.TemperatureMinUsed is { } low)
            {
                Assert.Equal((low, low + 1, low, low + 1, low + 1), (m.PhMinUsed!.Value, m.PhMaxUsed!.Value, m.SalinityMinUsed!.Value, m.SalinityMaxUsed!.Value, m.TemperatureMaxUsed!.Value));
            }
        });
    }

    private static string Base(WaterSqlServerZone seeded) => $"/api/zones/{seeded.Zone.Id}/water-measurements";

    private static async Task<(string Username, string Password)> OwnerAsync(SqlServerApiFactory api)
    {
        var password = NewPassword();
        return ((await api.AddAccountAsync(NewCode(), password)).Username, password);
    }

    private static async Task<HttpClient> LoginAsync(SqlServerApiFactory api, (string Username, string Password) owner)
    {
        var client = api.CreateClient();
        var tokens = await LoginTokensAsync(client, owner.Username, owner.Password);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>
    /// Columns, indexes, foreign keys and the status catalog that the migration touches, as sorted lines of text. Column
    /// defaults are left out: EF adds a DEFAULT constraint for each AddColumn with a default value, which no code reads.
    /// </summary>
    private static async Task<List<string>> ShapeAsync(OmtsDbContext db)
    {
        const string Columns =
            "SELECT TABLE_NAME COLLATE DATABASE_DEFAULT + '.' + COLUMN_NAME COLLATE DATABASE_DEFAULT + ' ' + DATA_TYPE COLLATE DATABASE_DEFAULT + ' ' + ISNULL(CAST(CHARACTER_MAXIMUM_LENGTH AS nvarchar(10)), '') + ' ' + ISNULL(CAST(NUMERIC_PRECISION AS nvarchar(10)), '') + ' ' + ISNULL(CAST(NUMERIC_SCALE AS nvarchar(10)), '') + ' ' + IS_NULLABLE COLLATE DATABASE_DEFAULT AS [Value] FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME IN (N'WaterMeasurements', N'FarmingZones')";
        const string Indexes =
            "SELECT OBJECT_NAME(i.object_id) COLLATE DATABASE_DEFAULT + '.' + i.name COLLATE DATABASE_DEFAULT + ' unique=' + CAST(i.is_unique AS nvarchar(1)) + ' filter=' + ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, '') + ' on ' + STRING_AGG(c.name COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS [Value] FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.object_id IN (OBJECT_ID(N'WaterMeasurements'), OBJECT_ID(N'FarmingZones')) GROUP BY i.object_id, i.name, i.is_unique, i.filter_definition";
        const string ForeignKeys =
            "SELECT OBJECT_NAME(parent_object_id) COLLATE DATABASE_DEFAULT + ' fk ' + name COLLATE DATABASE_DEFAULT + ' -> ' + OBJECT_NAME(referenced_object_id) COLLATE DATABASE_DEFAULT + ' ' + delete_referential_action_desc COLLATE DATABASE_DEFAULT AS [Value] FROM sys.foreign_keys WHERE parent_object_id IN (OBJECT_ID(N'WaterMeasurements'), OBJECT_ID(N'FarmingZones'))";
        const string Statuses =
            "SELECT 'status ' + CAST(Id AS nvarchar(10)) + ' ' + Code + ' ' + DisplayName + ' ' + [Group] + ' ' + CAST(IsFinal AS nvarchar(1)) + CAST(IsInitial AS nvarchar(1)) + ' ' + ISNULL(Meaning, '') + ' ' + CAST(SortOrder AS nvarchar(10)) AS [Value] FROM Statuses WHERE [Group] LIKE N'WATER%'";
        const string Transitions =
            "SELECT 'transition ' + CAST(Id AS nvarchar(10)) + ' ' + CAST(FromStatusId AS nvarchar(10)) + '>' + CAST(ToStatusId AS nvarchar(10)) + ' ' + ISNULL(AllowedPerformers, '') + ' ' + ISNULL(GuardCondition, '') + ' ' + CAST(ReasonRequired AS nvarchar(1)) + ' ' + ISNULL(TriggerEvent, '') + ' ' + ISNULL(UseCases, '') AS [Value] FROM StatusTransitions WHERE Id IN (10, 11, 55, 56, 80)";

        var lines = new List<string>();
        foreach (var sql in new[] { Columns, Indexes, ForeignKeys, Statuses, Transitions })
        {
            lines.AddRange(await db.Database.SqlQueryRaw<string>(sql).ToListAsync());
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    // Only constant SQL of this test class: no value from outside is concatenated.
    private static Task<int> ScalarAsync(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
