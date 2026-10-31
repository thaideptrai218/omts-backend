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
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>Notifications v2 on SQL Server: the NotificationsV2 migration and the ACCOUNT-07/08 queries.</summary>
public class NotificationSqlServerTests
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    // Dev data only: the old category × channel switches are reset, rows kept so far become list notifications.
    [SqlServerFact]
    public async Task Migration_resets_the_old_preferences_keeps_the_rows_in_the_list_and_renames_failed_to_not_sent()
    {
        await using var database = SqlServerTestDatabase.Create();
        await using var db = database.CreateContext();
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.EndsWith("_NotificationsV2", StringComparison.Ordinal)) - 1]);
        var (account, notification) = (Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Accounts (Id, Username, DisplayName, PasswordHash, SubjectType, StatusId, FailedLoginCount, MustChangePassword, CreatedAt, NotificationPreferences) VALUES ({account}, N'old', N'Old', N'x', N'CooperativeOwner', 301, 0, 0, SYSDATETIMEOFFSET(), N'[{{\"Category\":\"WORK\",\"Channel\":\"Push\",\"Enabled\":false}}]')");
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Notifications (Id, RecipientAccountId, Type, Title, Body, CreatedAt, IsMandatory) VALUES ({notification}, {account}, N'WATER_ALERT', N'Water quality alert', N'pH at zone A exceeds the set threshold.', SYSDATETIMEOFFSET(), 1)");

        await db.Database.MigrateAsync();

        var migrated = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == account);
        var row = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == notification);
        var notSent = await db.Statuses.AsNoTracking().SingleAsync(s => s.Id == StatusIds.MessageDelivery.NotSent);
        Assert.Empty(migrated.NotificationPreferences);
        Assert.Equal((NotificationChannel.InApp, (string?)null, (int?)null), (row.Channel, row.MessageCode, row.DeliveryStatusId));
        Assert.Equal(("NOT_SENT", "Not sent", StatusGroups.MessageDelivery), (notSent.Code, notSent.DisplayName, notSent.Group));
    }

    [SqlServerFact]
    public async Task On_SQL_Server_the_list_preferences_read_and_open_work_end_to_end()
    {
        await using var database = SqlServerTestDatabase.Create();
        await database.MigrateAsync();
        await using var api = new SqlServerApiFactory(database);
        var owner = await api.AddAccountAsync(NewCode(), TestPassword);
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await LoginTokensAsync(client, owner.Username, TestPassword)).GetProperty("accessToken").GetString());
        var zone = new FarmingZone { Cooperative = new Cooperative { FullName = "HTX Trung Nam" }, Code = NewCode(), Name = "Vùng Cái Rồng" };
        await using (var db = database.CreateContext())
        {
            db.FarmingZones.Add(zone);
            await db.SaveChangesAsync();
        }

        var saved = await client.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = "WATER_MEASUREMENT_OVERDUE", push = false } } });
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(
                [owner.Id],
                new NotificationContent(
                    NotificationTypes.WaterMeasurementOverdue, "Water measurement overdue", UserMessages.WaterMeasurementOverdue("Vùng Cái Rồng", "01/10/2026"),
                    new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code)),
                CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var list = await client.GetFromJsonAsync<JsonElement>("/api/me/notifications?unreadOnly=true");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        var preferences = await client.GetFromJsonAsync<JsonElement>("/api/me/notification-preferences");
        var opened = await client.GetAsync($"/api/me/notifications/{item.GetProperty("id").GetGuid()}/open");
        var readAll = await (await client.PostAsync("/api/me/notifications/read-all", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        Assert.Equal("Vùng Cái Rồng", item.GetProperty("messageParams").GetProperty("zone_name").GetString());
        Assert.False(preferences.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("type").GetString() == "WATER_MEASUREMENT_OVERDUE").GetProperty("push").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal((1, 0), (readAll.GetProperty("markedRead").GetInt32(), readAll.GetProperty("unreadCount").GetInt32()));
    }
}
