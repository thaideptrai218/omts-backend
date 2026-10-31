using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Persistence;
using static OMTS.Api.IntegrationTests.Audit.AuditTestKit;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Audit;

/// <summary>
/// QA of OMTS-216 PR 0.6 on SQL Server: ties and far pages, LIKE pattern characters in the record code, instants stored
/// with another offset, and the AuditSearch migration down and up again.
/// </summary>
public class AuditSearchSqlServerQaTests
{
    [SqlServerFact]
    public async Task Events_of_the_same_instant_page_without_gaps_or_repeats_and_far_pages_are_empty()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var client = await OwnerClientAsync(api);
        var (tag, at) = (NewTag(), DateTimeOffset.UtcNow.AddMinutes(-30));
        await SeedAsync(database, [
            .. Enumerable.Range(1, 23).Select(i => Event($"{tag}-{i:D2}", at)),
            // 100 ns apart: datetimeoffset(7) keeps them apart from the tie.
            Event($"{tag}-NEWER", at.AddTicks(1)),
            Event($"{tag}-OLDER", at.AddTicks(-1))]);

        var paged = new List<string>();
        for (var page = 1; page <= 6; page++)
        {
            paged.AddRange(Codes(await client.SearchAsync($"recordCode={tag}&pageSize=5&page={page}")));
        }

        var whole = Codes(await client.SearchAsync($"recordCode={tag}&pageSize=100"));
        Assert.Equal(25, whole.Distinct().Count());
        Assert.Equal(whole, paged);
        Assert.Equal(($"{tag}-NEWER", $"{tag}-OLDER"), (whole[0], whole[^1]));
        Assert.Equal(whole, Codes(await client.SearchAsync($"recordCode={tag}&pageSize=100")));
        var far = await client.SearchAsync($"recordCode={tag}&page={int.MaxValue}&pageSize={int.MaxValue}");
        Assert.Empty(Codes(far));
        Assert.Equal(25, far.GetProperty("page").GetProperty("total").GetInt32());
    }

    // "_" and "%" are covered by the coder's test; "[", "^", "\" and "'" are LIKE or SQL characters as well.
    [SqlServerFact]
    public async Task Record_codes_with_like_pattern_characters_match_literally_and_without_case()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var client = await OwnerClientAsync(api);
        var tag = NewTag();
        string[] codes = [$"{tag}[A]1", $"{tag}A1", $"{tag}B1", $@"{tag}\X", $@"{tag}X", $"{tag}'Q", $"{tag}%_Z", $"{tag}AB_Z", $"{tag}[^A]"];
        var at = DateTimeOffset.UtcNow.AddMinutes(-20);
        await SeedAsync(database, codes.Select((c, i) => Event(c, at.AddSeconds(i))).ToArray());

        foreach (var (prefix, expected) in new (string, string[])[]
        {
            ($"{tag}[A]", [$"{tag}[A]1"]),
            ($"{tag}[", [$"{tag}[A]1", $"{tag}[^A]"]),
            ($"{tag}[^", [$"{tag}[^A]"]),
            ($@"{tag}\", [$@"{tag}\X"]),
            ($"{tag}'", [$"{tag}'Q"]),
            ($"{tag}%_", [$"{tag}%_Z"]),
            ($"{tag.ToLowerInvariant()}a1", [$"{tag}A1"]),
        })
        {
            var found = Codes(await client.SearchAsync($"recordCode={Uri.EscapeDataString(prefix)}"));
            Assert.True(expected.Order().SequenceEqual(found.Order()), $"\"{prefix}\" found [{string.Join(", ", found)}]");
        }
    }

    // SQL Server compares datetimeoffset values as instants: a row stored in UTC and one stored at +07:00 are placed by
    // the moment they happened, in the order and at both ends of the period.
    [SqlServerFact]
    public async Task The_period_and_the_order_compare_instants_whatever_offset_they_were_stored_with()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var api = await StartAsync(database);
        var client = await OwnerClientAsync(api);
        var tag = NewTag();
        var (from, to) = (new DateOnly(2025, 3, 10), new DateOnly(2025, 3, 12));
        var vietnam = TimeSpan.FromHours(7);
        await SeedAsync(database,
            Event($"{tag}-BEFORE", StartOf(from).AddTicks(-1).ToUniversalTime()),
            Event($"{tag}-FIRST", StartOf(from)),
            Event($"{tag}-X1", new DateTimeOffset(2025, 3, 11, 10, 0, 0, vietnam)),
            Event($"{tag}-X2", new DateTimeOffset(2025, 3, 11, 4, 0, 0, TimeSpan.Zero)),
            Event($"{tag}-LAST", StartOf(to.AddDays(1)).AddTicks(-1).ToUniversalTime()),
            Event($"{tag}-AFTER", StartOf(to.AddDays(1))));

        var found = Codes(await client.SearchAsync($"recordCode={tag}&from={Date(from)}&to={Date(to)}"));

        Assert.Equal([$"{tag}-LAST", $"{tag}-X2", $"{tag}-X1", $"{tag}-FIRST"], found);
    }

    [SqlServerFact]
    public async Task Migration_down_restores_the_old_schema_with_the_rows_kept_and_up_again_matches_the_model()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var auditSearch = all.Single(m => m.EndsWith("_AuditSearch", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(auditSearch);
        var account = new Account { Username = NewCode(), DisplayName = "Owner", SubjectType = SubjectType.CooperativeOwner };
        db.Accounts.Add(account);
        db.AuditEvents.AddRange(
            new AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow, ActorAccountId = account.Id },
            new AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow, ActorEmployeeId = Guid.NewGuid() },
            new AuditEvent { Action = "A.Done", RecordType = "A", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(all[all.IndexOf(auditSearch) - 1]);

        Assert.Equal(
            "IX_AuditEvents_ActorAccountId,IX_AuditEvents_OccurredAt,IX_AuditEvents_RecordType_RecordId,IX_AuditEvents_ZoneId_OccurredAt",
            await ScalarAsync<string>(db, "SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID('AuditEvents') AND is_primary_key = 0 AND name IS NOT NULL"));
        Assert.Equal("ViewScope:nvarchar:200:1", await ScalarAsync<string>(db,
            "SELECT c.name + ':' + t.name + ':' + CAST(c.max_length AS nvarchar) + ':' + CAST(c.is_nullable AS nvarchar) AS [Value] FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('AuditEvents') AND c.name = 'ViewScope'"));
        Assert.Equal(0, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID('AuditEvents') AND name = 'ActorEmployeeId'"));
        Assert.Equal(3, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM AuditEvents"));

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(3, await db.AuditEvents.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.ActorAccountId == account.Id));
    }

    private static Task<T> ScalarAsync<T>(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<T>(sql).SingleAsync();

    private static async Task<SqlServerApiFactory> StartAsync(SqlServerTestDatabase database)
    {
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        return new SqlServerApiFactory(database);
    }

    private static async Task<HttpClient> OwnerClientAsync(SqlServerApiFactory api)
    {
        var password = NewPassword();
        var owner = await api.AddAccountAsync(NewCode(), password);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(client, owner.Username, password)).GetProperty("accessToken").GetString());
        return client;
    }

    private static async Task SeedAsync(SqlServerTestDatabase database, params AuditEvent[] events)
    {
        await using var db = database.CreateContext();
        db.AuditEvents.AddRange(events);
        await db.SaveChangesAsync();
    }
}
