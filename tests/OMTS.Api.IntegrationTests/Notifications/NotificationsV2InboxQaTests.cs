using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Identity;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Notifications;

/// <summary>
/// QA of OMTS-216 PR 0.5 on ACCOUNT-08 (BR-ACCOUNT-12, GFR-02): paging bounds, own InApp rows only, the same 404 for a
/// row of another account as for an unknown id, and the access re-check of each role when a record is opened.
/// </summary>
public class NotificationsV2InboxQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Generated per run, so no password-like literal is committed (secret scanners).
    private static readonly string TestPassword = NewPassword();

    [Fact]
    public async Task Paging_values_out_of_range_are_clamped_and_a_page_past_the_end_is_empty()
    {
        var (owner, client) = await AccountAsync(SubjectType.CooperativeOwner);
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await factory.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(await factory.AddZoneAsync(NewCode())));
        }

        var cases = new (string Query, int Page, int PageSize, int Count)[]
        {
            ("?pageSize=0", 1, 1, 1), ("?pageSize=-5", 1, 1, 1), ("?pageSize=1000", 1, 100, 3), ("?page=0&pageSize=2", 1, 2, 2),
            ("?page=-7&pageSize=2", 1, 2, 2), ("?page=2&pageSize=2", 2, 2, 1), ("?page=3&pageSize=2", 3, 2, 0),
            ($"?page={int.MaxValue}&pageSize=100", int.MaxValue / 100, 100, 0),
        };
        foreach (var (query, page, pageSize, count) in cases)
        {
            var result = await client.NotificationPageAsync(query);
            Assert.True(
                (page, pageSize, count, 3, 3) == (result.GetProperty("page").GetInt32(), result.GetProperty("pageSize").GetInt32(), result.Items().Count,
                    result.GetProperty("total").GetInt32(), result.GetProperty("unreadCount").GetInt32()),
                $"{query}: {result}");
        }

        // Two pages of two cover the list once, newest first.
        var all = (await client.NotificationPageAsync()).NotificationIds();
        var paged = (await client.NotificationPageAsync("?page=1&pageSize=2")).NotificationIds()
            .Concat((await client.NotificationPageAsync("?page=2&pageSize=2")).NotificationIds());
        Assert.Equal(all, paged);
    }

    // BR-SALE-09: SMS and Zalo OA sendings kept on the account are never in the list: not readable, not openable and
    // not marked by read-all.
    [Fact]
    public async Task Sms_and_zalo_oa_rows_of_the_account_cannot_be_read_opened_or_marked_by_read_all()
    {
        var (owner, client) = await AccountAsync(SubjectType.CooperativeOwner);
        var zone = await factory.AddZoneAsync(NewCode());
        await factory.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(zone));
        var sendings = new[] { NotificationChannel.Sms, NotificationChannel.ZaloOa }.Select(channel => new Notification
        {
            RecipientAccountId = owner.Id, Channel = channel, Type = "SALES_INVOICE", Title = "Invoice", PhoneNumber = owner.PhoneNumber,
            DeliveryStatusId = StatusIds.MessageDelivery.Sent, RelatedRecordType = RelatedRecordTypes.FarmingZone, RelatedRecordId = zone.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(5),
        }).ToList();
        await factory.UseDbAsync(db => db.Notifications.AddRange(sendings));

        foreach (var sending in sendings)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.MarkNotificationReadAsync(sending.Id)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.OpenNotificationAsync(sending.Id)).StatusCode);
        }

        var all = await (await client.MarkAllNotificationsReadAsync()).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((1, 0), (all.GetProperty("markedRead").GetInt32(), all.GetProperty("unreadCount").GetInt32()));
        var ids = sendings.Select(s => s.Id).ToList();
        Assert.All(await factory.QueryAsync(db => db.Notifications.Where(n => ids.Contains(n.Id)).ToListAsync()), n => Assert.Null(n.ReadAt));
    }

    // Own rows only: the row of another account answers exactly as an id that does not exist, and stays unread.
    [Fact]
    public async Task A_row_of_another_account_answers_the_same_404_as_an_unknown_id()
    {
        var (owner, ownerClient) = await AccountAsync(SubjectType.CooperativeOwner);
        var (_, otherOwner) = await AccountAsync(SubjectType.CooperativeOwner);
        var (_, trader) = await AccountAsync(SubjectType.Trader);
        await factory.NotifyAsync([owner.Id], NotificationsV2QaKit.OverdueReminder(await factory.AddZoneAsync(NewCode())));
        var id = (await ownerClient.NotificationPageAsync()).NotificationIds().Single();

        foreach (var client in new[] { otherOwner, trader })
        {
            Assert.Empty((await client.NotificationPageAsync()).Items());
            foreach (var call in new Func<Guid, Task<HttpResponseMessage>>[] { client.MarkNotificationReadAsync, client.OpenNotificationAsync })
            {
                var theirs = Visible(await (await call(id)).ProblemAsync());
                var unknown = Visible(await (await call(Guid.NewGuid())).ProblemAsync());
                Assert.Equal((404, "NOT_FOUND"), (theirs.Status, theirs.Code));
                Assert.Equal(unknown, theirs);
            }

            Assert.Equal(0, (await (await client.MarkAllNotificationsReadAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("markedRead").GetInt32());
        }

        Assert.Equal(1, (await ownerClient.NotificationPageAsync()).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task A_notification_without_a_related_record_opens_as_not_found_not_as_a_removed_record()
    {
        var (owner, client) = await AccountAsync(SubjectType.CooperativeOwner);
        await factory.NotifyAsync([owner.Id], new NotificationContent(
            NotificationTypes.StaffAccountLockReminder, "Lock reminder", new UserMessage("MSG-LABOR-02", "Lock the staff account.")));
        var id = (await client.NotificationPageAsync()).NotificationIds().Single();

        var open = await client.OpenNotificationAsync(id);

        Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
        Assert.Equal("NOT_FOUND", await open.ErrorCodeAsync());
    }

    // GFR-02 at step 5: the same notification of a zone opens only for the roles that may open the zone's water records.
    [Fact]
    public async Task Opening_rechecks_the_role_and_the_zone_of_each_recipient()
    {
        var (zone, otherZone) = (await factory.AddZoneAsync(NewCode()), await factory.AddZoneAsync(NewCode()));
        var actors = new (string Name, (Account Account, HttpClient Client) Actor, HttpStatusCode Expected)[]
        {
            ("owner", await AccountAsync(SubjectType.CooperativeOwner), HttpStatusCode.OK),
            ("manager of the zone", await StaffAsync(StaffRole.ZoneManager, zone), HttpStatusCode.OK),
            ("manager of all zones", await StaffAsync(StaffRole.ZoneManager, null), HttpStatusCode.OK),
            ("manager of another zone", await StaffAsync(StaffRole.ZoneManager, otherZone), HttpStatusCode.Forbidden),
            ("accountant of all zones", await StaffAsync(StaffRole.Accountant, null), HttpStatusCode.Forbidden),
            ("trader", await AccountAsync(SubjectType.Trader), HttpStatusCode.Forbidden),
        };
        await factory.NotifyAsync(actors.Select(a => a.Actor.Account.Id).ToList(), NotificationsV2QaKit.OverdueReminder(zone));

        foreach (var (name, (_, client), expected) in actors)
        {
            var id = (await client.NotificationPageAsync()).NotificationIds().Single();
            var open = await client.OpenNotificationAsync(id);
            Assert.True(open.StatusCode == expected, $"{name}: {(int)open.StatusCode} {await open.Content.ReadAsStringAsync()}");
            if (expected == HttpStatusCode.Forbidden)
            {
                Assert.Equal("MSG-GEN-08", await open.ErrorCodeAsync());
            }
            else
            {
                Assert.Equal(zone.Id, (await open.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("zoneId").GetGuid());
            }
        }
    }

    // ACCOUNT-09.2 logs a locked account out everywhere: its list is unreachable, and kept for after the unlock.
    [Fact]
    public async Task A_locked_staff_account_loses_its_list_and_finds_it_unchanged_after_the_unlock()
    {
        var (_, ownerClient) = await AccountAsync(SubjectType.CooperativeOwner);
        var zone = await factory.AddZoneAsync(NewCode());
        var (manager, client) = await StaffAsync(StaffRole.ZoneManager, zone);
        await factory.NotifyAsync([manager.Id], NotificationsV2QaKit.OverdueReminder(zone));
        var id = (await client.NotificationPageAsync()).NotificationIds().Single();

        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"/api/accounts/{manager.Id}/lock", new { reason = "Left the cooperative" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(NotificationsV2QaKit.ListPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.OpenNotificationAsync(id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.MarkNotificationReadAsync(id)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ownerClient.PostAsJsonAsync($"/api/accounts/{manager.Id}/unlock", new { reason = "Back at work" })).StatusCode);
        var again = await factory.LoginAsync(manager.Username, TestPassword);
        var item = (await again.NotificationPageAsync()).Items().Single();
        Assert.Equal((id, JsonValueKind.Null), (item.GetProperty("id").GetGuid(), item.GetProperty("readAt").ValueKind));
        Assert.Equal(HttpStatusCode.OK, (await again.OpenNotificationAsync(id)).StatusCode);
    }

    // Everything a client reads from a problem, except the trace id of the request.
    private static (int Status, string? Code, string? Title, string? Detail, string Fields) Visible(JsonElement problem) => (
        problem.GetProperty("status").GetInt32(),
        problem.GetProperty("code").GetString(),
        problem.GetProperty("title").GetString(),
        problem.GetProperty("detail").GetString(),
        string.Join(",", problem.EnumerateObject().Select(p => p.Name).Where(n => n != "traceId").Order()));

    private async Task<(Account Account, HttpClient Client)> AccountAsync(SubjectType subjectType)
    {
        var account = await factory.AddAccountAsync($"u{NewCode()}", TestPassword, subjectType);
        return (account, await factory.LoginAsync(account.Username, TestPassword));
    }

    private async Task<(Account Account, HttpClient Client)> StaffAsync(StaffRole role, FarmingZone? zone)
    {
        var account = await factory.AddAccountAsync($"u{NewCode()}", TestPassword, SubjectType.Staff);
        await factory.GrantAsync(account, role, zone);
        return (account, await factory.LoginAsync(account.Username, TestPassword));
    }
}
