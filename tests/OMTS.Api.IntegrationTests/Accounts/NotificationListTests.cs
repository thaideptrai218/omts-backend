using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Accounts;

/// <summary>
/// ACCOUNT-08 View notifications and BR-ACCOUNT-11/12 end to end: every notification is kept whatever the push
/// preference, push follows the type, the list is the caller's own (newest first, unread count, mark read, read all),
/// and opening the related record checks the access again.
/// </summary>
public class NotificationListTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    [Fact]
    public async Task A_notification_is_kept_in_the_list_with_its_message_code_although_its_push_is_turned_off()
    {
        var (owner, client) = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        var off = await client.PutAsJsonAsync("/api/me/notification-preferences", new { items = new[] { new { type = "WATER_MEASUREMENT_OVERDUE", push = false } } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        await NotifyAsync([owner.Id], OverdueReminder(zone));

        var item = Assert.Single((await ListAsync(client)).GetProperty("items").EnumerateArray());
        Assert.Equal("WATER_MEASUREMENT_OVERDUE", item.GetProperty("type").GetString());
        Assert.Equal("MSG-WATER-06", item.GetProperty("messageCode").GetString());
        Assert.Equal(zone.Name, item.GetProperty("messageParams").GetProperty("zone_name").GetString());
        Assert.Equal($"Zone {zone.Name} has had no water measurement since 01/10/2026. Please measure the water and record the result in OMTS.", item.GetProperty("body").GetString());
        Assert.Equal(("FarmingZone", zone.Id, zone.Code), (item.GetProperty("relatedRecordType").GetString(), item.GetProperty("relatedRecordId").GetGuid(), item.GetProperty("relatedRecordCode").GetString()));
        Assert.False(item.GetProperty("mandatory").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
        Assert.Empty(Pushes(owner));
    }

    [Fact]
    public async Task A_mandatory_type_is_pushed_even_when_its_stored_push_is_off_and_a_never_push_type_is_never_pushed()
    {
        var (owner, client) = await OwnerAsync();
        // The API refuses to turn a mandatory type off; a stored choice from before must not stop its push either.
        await factory.UpdateAsync<Account>(owner.Id, a => a.NotificationPreferences.Add(new NotificationPreference { Type = NotificationTypes.WaterAlert, Push = false }));
        var zone = await factory.AddZoneAsync(NewCode());

        await NotifyAsync([owner.Id], new NotificationContent(
            NotificationTypes.WaterAlert, "Water quality alert", UserMessages.WaterAlert("", zone.Name, "–", "06:30 20/10/2026", "pH 9.1 is too high (allowed 7.5–8.5)"),
            new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code)));
        await NotifyAsync([owner.Id], new NotificationContent(
            NotificationTypes.StaffAccountLockReminder, "Lock reminder", new UserMessage("MSG-LABOR-02", "Lock the staff account."),
            new RelatedRecord(RelatedRecordTypes.Account, owner.Id, owner.Username)));

        var types = (await ListAsync(client)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()).ToList();
        Assert.Equal(["STAFF_ACCOUNT_LOCK_REMINDER", "WATER_ALERT"], types.Order());
        var pushed = Assert.Single(Pushes(owner));
        Assert.StartsWith("WATER_ALERT:", pushed.Text);
    }

    [Fact]
    public async Task The_list_is_newest_first_with_the_unread_count_and_marks_one_or_all_as_read()
    {
        var (owner, client) = await OwnerAsync();
        var (other, otherClient) = await OwnerAsync();
        var zones = new[] { await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()) };
        foreach (var zone in zones)
        {
            await NotifyAsync([owner.Id, other.Id], OverdueReminder(zone));
        }

        // Fixed times so the order does not depend on the clock; an SMS sending of the account is never listed.
        var start = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        await factory.UseDbAsync(db =>
        {
            foreach (var (row, index) in db.Notifications.Where(n => n.RecipientAccountId == owner.Id).AsEnumerable()
                .OrderBy(n => Array.FindIndex(zones, z => z.Id == n.RelatedRecordId)).Select((n, i) => (n, i)))
            {
                row.CreatedAt = start.AddHours(index);
            }

            db.Notifications.Add(new Notification
            {
                RecipientAccountId = owner.Id, Channel = NotificationChannel.Sms, Type = "SALES_INVOICE_SMS", Title = "Invoice", PhoneNumber = owner.PhoneNumber,
                DeliveryStatusId = OMTS.Domain.Constants.StatusIds.MessageDelivery.NotSent, CreatedAt = start.AddDays(1),
            });
        });

        var page = await ListAsync(client);
        var ids = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(zones.Reverse().Select(z => z.Code), page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("relatedRecordCode").GetString()));
        Assert.Equal((3, 3), (page.GetProperty("total").GetInt32(), page.GetProperty("unreadCount").GetInt32()));

        var read = await client.PostAsync($"/api/me/notifications/{ids[1]}/read", null);
        var readBody = await read.Content.ReadFromJsonAsync<JsonElement>();
        var readAt = readBody.GetProperty("readAt").GetDateTimeOffset();
        var readAgain = await (await client.PostAsync($"/api/me/notifications/{ids[1]}/read", null)).Content.ReadFromJsonAsync<JsonElement>();
        var unread = await ListAsync(client, "?unreadOnly=true");
        var notMine = await otherClient.PostAsync($"/api/me/notifications/{ids[0]}/read", null);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(readAt, readAgain.GetProperty("readAt").GetDateTimeOffset());
        Assert.Equal([ids[0], ids[2]], unread.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal((2, 2), (unread.GetProperty("total").GetInt32(), unread.GetProperty("unreadCount").GetInt32()));
        Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);

        var all = await (await client.PostAsync("/api/me/notifications/read-all", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((2, 0), (all.GetProperty("markedRead").GetInt32(), all.GetProperty("unreadCount").GetInt32()));
        Assert.Equal(0, (await ListAsync(client)).GetProperty("unreadCount").GetInt32());
        Assert.Equal(3, (await ListAsync(otherClient)).GetProperty("unreadCount").GetInt32());

        var paged = await ListAsync(client, "?page=2&pageSize=2");
        Assert.Equal([ids[2]], paged.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    // BR-ACCOUNT-12: a user who lost the zone–role pair since the notification was sent cannot open its record.
    [Fact]
    public async Task Opening_the_record_after_the_zone_pair_was_revoked_answers_msg_gen_08()
    {
        var (_, ownerClient) = await OwnerAsync();
        var (zoneA, zoneB) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var manager = await factory.AddAccountAsync(NewCode(), TestPassword, SubjectType.Staff);
        var pairA = await factory.GrantAsync(manager, StaffRole.ZoneManager, zoneA);
        await factory.GrantAsync(manager, StaffRole.ZoneManager, zoneB);
        var client = await factory.LoginAsync(manager.Username, TestPassword);
        await NotifyAsync([manager.Id], OverdueReminder(zoneA));
        var id = (await ListAsync(client)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var before = await client.GetAsync($"/api/me/notifications/{id}/open");
        var revoked = await ownerClient.PostAsJsonAsync($"/api/accounts/{manager.Id}/grants/{pairA.Id}/revoke", new { reason = "Moved to zone B" });
        var after = await client.GetAsync($"/api/me/notifications/{id}/open");

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var opened = await before.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("FarmingZone", zoneA.Id, zoneA.Id), (opened.GetProperty("recordType").GetString(), opened.GetProperty("recordId").GetGuid(), opened.GetProperty("zoneId").GetGuid()));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("MSG-GEN-08", await after.ErrorCodeAsync());
        // The notification stays in the list.
        Assert.Equal(1, (await ListAsync(client)).GetProperty("total").GetInt32());
    }

    // ACCOUNT-08.0.E1: the record was removed; the notification is still shown.
    [Fact]
    public async Task Opening_a_removed_record_answers_msg_account_22_and_keeps_the_notification()
    {
        var (owner, client) = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        await NotifyAsync([owner.Id], OverdueReminder(zone));
        var id = (await ListAsync(client)).GetProperty("items")[0].GetProperty("id").GetGuid();
        await factory.UseDbAsync(db => db.FarmingZones.Remove(db.FarmingZones.Single(z => z.Id == zone.Id)));

        var open = await client.GetAsync($"/api/me/notifications/{id}/open");

        Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
        Assert.Equal("MSG-ACCOUNT-22", await open.ErrorCodeAsync());
        Assert.Equal($"Farming zone {zone.Code} no longer exists.", (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal(id, (await ListAsync(client)).GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Opening_a_notification_of_another_account_answers_not_found()
    {
        var (owner, _) = await OwnerAsync();
        var (_, otherClient) = await OwnerAsync();
        var zone = await factory.AddZoneAsync(NewCode());
        await NotifyAsync([owner.Id], OverdueReminder(zone));
        var id = await factory.QueryAsync(db => db.Notifications.Where(n => n.RecipientAccountId == owner.Id).Select(n => n.Id).SingleAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/me/notifications/{id}/open")).StatusCode);
    }

    private async Task<(Account Account, HttpClient Client)> OwnerAsync()
    {
        var account = await factory.AddAccountAsync(NewCode(), TestPassword);
        return (account, await factory.LoginAsync(account.Username, TestPassword));
    }

    private static NotificationContent OverdueReminder(FarmingZone zone) => new(
        NotificationTypes.WaterMeasurementOverdue,
        "Water measurement overdue",
        UserMessages.WaterMeasurementOverdue(zone.Name, "01/10/2026"),
        new RelatedRecord(RelatedRecordTypes.FarmingZone, zone.Id, zone.Code));

    /// <summary>Dispatch and save, as a module does inside its use case.</summary>
    private async Task NotifyAsync(IReadOnlyCollection<Guid> recipients, NotificationContent content)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>().NotifyAsync(recipients, content, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"/api/me/notifications{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private IReadOnlyList<FakeMessage> Pushes(Account account) =>
        factory.Services.GetRequiredService<FakePushSender>().Inbox.List(account.Id.ToString());
}
