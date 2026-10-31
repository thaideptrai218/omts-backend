using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Infrastructure.Persistence;
using Xunit.Abstractions;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// QA of OMTS-216 PR 0.5 on SQL Server: the NotificationsV2 migration down and up again, and ACCOUNT-07/08 under
/// concurrent requests (read-all racing new notifications, mark-read races, ties in the list order, preference saves).
/// </summary>
public class NotificationsV2SqlServerQaTests(ITestOutputHelper output)
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    private const string NewColumns =
        "N'Channel', N'DeliveryStatusAt', N'DeliveryStatusId', N'MessageCode', N'MessageParams', N'PhoneNumber', N'ProviderErrorCode', N'ProviderMessageId', N'RelatedRecordId'";

    private const string NewIndexes =
        "N'IX_Notifications_DeliveryStatusId', N'IX_Notifications_RecipientAccountId_Channel_CreatedAt', N'IX_Notifications_RelatedRecordType_RelatedRecordId'";

    [SqlServerFact]
    public async Task Migration_down_restores_the_old_schema_and_statuses_with_rows_kept_and_up_again_matches_the_model()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        var v2 = all.Single(m => m.EndsWith("_NotificationsV2", StringComparison.Ordinal));
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(v2);
        var (account, inApp, sms) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, NotificationPreferences) VALUES ({account}, N'qa', N'QA', N'x', N'CooperativeOwner', {StatusIds.Account.Active}, 0, 0, SYSDATETIMEOFFSET(), N'[]')");
        // A row written without a channel is a list notification (column default).
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Notifications (Id, RecipientAccountId, Type, Title, CreatedAt, IsMandatory) VALUES ({inApp}, {account}, N'WATER_ALERT', N'Water quality alert', SYSDATETIMEOFFSET(), 1)");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Notifications (Id, RecipientAccountId, Channel, Type, Title, CreatedAt, IsMandatory, PhoneNumber, DeliveryStatusId, DeliveryStatusAt) VALUES ({sms}, {account}, N'Sms', N'SALES_INVOICE', N'Invoice', SYSDATETIMEOFFSET(), 0, N'0912345678', {StatusIds.MessageDelivery.NotSent}, SYSDATETIMEOFFSET())");
        Assert.Equal("InApp", await ScalarAsync<string>(db, $"SELECT [Channel] AS [Value] FROM [Notifications] WHERE [Id] = '{inApp}'"));
        Assert.Equal((9, 3, 1), await SchemaAsync(db));
        Assert.Equal("NOT_SENT", await ScalarAsync<string>(db, "SELECT [Code] AS [Value] FROM [Statuses] WHERE [Id] = 1603"));

        await migrator.MigrateAsync(all[all.IndexOf(v2) - 1]);

        Assert.Equal((0, 0, 0), await SchemaAsync(db));
        Assert.Equal(2, await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM [Notifications]"));
        Assert.Equal(("FAILED", "Failed"), (
            await ScalarAsync<string>(db, "SELECT [Code] AS [Value] FROM [Statuses] WHERE [Id] = 1603"),
            await ScalarAsync<string>(db, "SELECT [DisplayName] AS [Value] FROM [Statuses] WHERE [Id] = 1603")));
        Assert.Equal(("UC70", "System; time"), (
            await ScalarAsync<string>(db, "SELECT [UseCases] AS [Value] FROM [StatusTransitions] WHERE [Id] = 54"),
            await ScalarAsync<string>(db, "SELECT [TriggerEvent] AS [Value] FROM [StatusTransitions] WHERE [Id] = 54")));

        await db.Database.MigrateAsync();

        Assert.Equal((9, 3, 1), await SchemaAsync(db));
        Assert.Equal("NOT_SENT", await ScalarAsync<string>(db, "SELECT [Code] AS [Value] FROM [Statuses] WHERE [Id] = 1603"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // A notification committed while read-all runs is either marked by it or left unread, never lost or marked twice;
    // nothing is marked read before it was created.
    [SqlServerFact]
    public async Task Read_all_racing_new_notifications_marks_each_one_once_or_leaves_it_unread()
    {
        var (database, api, owner, client, zone) = await SetUpAsync();
        await using var databaseLifetime = database;
        await using var apiLifetime = api;
        const int rounds = 12;
        var marked = 0;
        for (var round = 0; round < rounds; round++)
        {
            var notify = Task.Run(() => api.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(zone)));
            var readAll = client.MarkAllNotificationsReadAsync();
            await Task.WhenAll(notify, readAll);
            var response = await readAll;
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            marked += (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("markedRead").GetInt32();
        }

        var unread = (await client.NotificationPageAsync()).GetProperty("unreadCount").GetInt32();
        var rows = await ReadAsync(database, db => db.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == owner.Id).ToListAsync());
        output.WriteLine($"marked by read-all: {marked}, still unread: {unread}");
        Assert.Equal(rounds, rows.Count);
        Assert.Equal(rounds, marked + unread);
        Assert.All(rows, n => Assert.True(n.ReadAt is null || n.ReadAt >= n.CreatedAt, $"{n.Id}: read {n.ReadAt:O} before created {n.CreatedAt:O}"));
    }

    [SqlServerFact]
    public async Task Concurrent_mark_read_and_read_all_on_the_same_rows_all_succeed_and_leave_every_row_read()
    {
        var (database, api, owner, client, zone) = await SetUpAsync();
        await using var databaseLifetime = database;
        await using var apiLifetime = api;
        for (var i = 0; i < 8; i++)
        {
            await api.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(zone));
        }

        var ids = (await client.NotificationPageAsync()).NotificationIds();
        var responses = await Task.WhenAll(ids.Select(client.MarkNotificationReadAsync)
            .Concat(ids.Select(client.MarkNotificationReadAsync))
            .Concat(Enumerable.Range(0, 3).Select(_ => client.MarkAllNotificationsReadAsync())));

        foreach (var response in responses)
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }

        Assert.Equal(0, (await client.NotificationPageAsync()).GetProperty("unreadCount").GetInt32());
        Assert.All(await ReadAsync(database, db => db.Notifications.AsNoTracking().Where(n => n.RecipientAccountId == owner.Id).ToListAsync()),
            n => Assert.NotNull(n.ReadAt));
    }

    // ACCOUNT-08 step 3 / BR-ACCOUNT-12: the time it was read is saved once. A mark-read that read the row while it was
    // still unread must not move the time a concurrent request already saved.
    [SqlServerFact(Skip = "QA finding OMTS-216 PR 0.5 #3 (mark-read reads then writes without a guard: a concurrent second request overwrites the saved read time)")]
    public async Task A_mark_read_that_loses_the_race_keeps_the_first_read_time()
    {
        var (database, api, owner, client, zone) = await SetUpAsync();
        await using var databaseLifetime = database;
        await using var apiLifetime = api;
        await api.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(zone));
        var id = (await client.NotificationPageAsync()).NotificationIds().Single();

        // Request A has read the row while it was unread; request B marks it read and commits first.
        using var requestA = api.Services.CreateScope();
        Assert.Null((await requestA.ServiceProvider.GetRequiredService<INotificationRepository>().GetAsync(owner.Id, id, CancellationToken.None))!.ReadAt);
        var first = (await (await client.MarkNotificationReadAsync(id)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readAt").GetDateTimeOffset();
        await Task.Delay(20);
        var late = await requestA.ServiceProvider.GetRequiredService<NotificationInboxService>().MarkReadAsync(owner.Id, id, CancellationToken.None);

        var stored = await ReadAsync(database, db => db.Notifications.AsNoTracking().Where(n => n.Id == id).Select(n => n.ReadAt).SingleAsync());
        Assert.Equal<(DateTimeOffset?, DateTimeOffset?)>((first, first), (stored, late.Value!.ReadAt));
    }

    // Rows with the same time keep one order across pages: no row is shown twice or skipped.
    [SqlServerFact]
    public async Task Rows_created_at_the_same_time_are_paged_without_gaps_or_repeats()
    {
        var (database, api, owner, client, zone) = await SetUpAsync();
        await using var databaseLifetime = database;
        await using var apiLifetime = api;
        for (var i = 0; i < 5; i++)
        {
            await api.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(zone));
        }

        var same = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var db = database.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE [Notifications] SET [CreatedAt] = {same} WHERE [RecipientAccountId] = {owner.Id}");
        }

        var all = (await client.NotificationPageAsync("?pageSize=100")).NotificationIds();
        var paged = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            paged.AddRange((await client.NotificationPageAsync($"?page={page}&pageSize=2")).NotificationIds());
        }

        Assert.Equal(5, all.Distinct().Count());
        Assert.Equal(all, paged);
    }

    // The preferences are one JSON value of the account row (optimistic concurrency): concurrent saves either succeed or
    // answer 409 MSG06, never 500, and the stored choice is one that was answered as saved.
    [SqlServerFact]
    public async Task Concurrent_preference_saves_answer_saved_or_409_and_never_lose_a_saved_choice_silently()
    {
        var (database, api, _, client, _) = await SetUpAsync();
        await using var databaseLifetime = database;
        await using var apiLifetime = api;

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => client.SavePushAsync(("WATER_MEASUREMENT_OVERDUE", i % 2 == 0))));

        var saved = new List<bool>();
        for (var i = 0; i < responses.Length; i++)
        {
            if (responses[i].StatusCode == HttpStatusCode.OK)
            {
                saved.Add(i % 2 == 0);
                continue;
            }

            Assert.True(responses[i].StatusCode == HttpStatusCode.Conflict, $"{(int)responses[i].StatusCode} {await responses[i].Content.ReadAsStringAsync()}");
            Assert.Equal("MSG06", await responses[i].ErrorCodeAsync());
        }

        output.WriteLine($"saved: {saved.Count}, 409: {responses.Length - saved.Count}");
        Assert.NotEmpty(saved);
        Assert.Contains((await client.OfferedTypesAsync())["WATER_MEASUREMENT_OVERDUE"].Push, saved);
    }

    private static async Task<(SqlServerTestDatabase Database, SqlServerApiFactory Api, Account Owner, HttpClient Client, FarmingZone Zone)> SetUpAsync()
    {
        var database = SqlServerTestDatabase.Create();
        var zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX Trung Nam" }, Code = NewCode(), Name = "Vùng Cái Rồng" };
        await using (var db = database.CreateContext())
        {
            await db.Database.MigrateAsync();
            db.FarmingZones.Add(zone);
            await db.SaveChangesAsync();
        }

        var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync($"u{NewCode()}", TestPassword);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(client, owner.Username, TestPassword)).GetProperty("accessToken").GetString());
        return (database, api, owner, client, zone);
    }

    /// <summary>New columns, new indexes and the new foreign key of Notifications that exist.</summary>
    private static async Task<(int Columns, int Indexes, int ForeignKeys)> SchemaAsync(OmtsDbContext db) => (
        await ScalarAsync<int>(db, $"SELECT COUNT(*) AS [Value] FROM sys.columns WHERE object_id = OBJECT_ID(N'Notifications') AND name IN ({NewColumns})"),
        await ScalarAsync<int>(db, $"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'Notifications') AND name IN ({NewIndexes})"),
        await ScalarAsync<int>(db, "SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE name = N'FK_Notifications_Statuses_DeliveryStatusId'"));

    // Only constant SQL of this test class: no value from outside is concatenated.
    private static Task<T> ScalarAsync<T>(OmtsDbContext db, string sql) => db.Database.SqlQueryRaw<T>(sql).SingleAsync();

    private static async Task<T> ReadAsync<T>(SqlServerTestDatabase database, Func<OmtsDbContext, Task<T>> query)
    {
        await using var db = database.CreateContext();
        return await query(db);
    }
}
