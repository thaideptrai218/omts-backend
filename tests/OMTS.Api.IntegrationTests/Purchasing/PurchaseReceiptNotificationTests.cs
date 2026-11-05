using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Notifications;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// BR-PURCHASE-05 / BR-ACCOUNT-11: the Owner is notified when a receipt is completed (MSG-PURCHASE-01, a normal type whose
/// push the Owner may turn off), also when the Owner completed it; a draft sends nothing. The message code is stored with
/// atomic parameters.
/// </summary>
public class PurchaseReceiptNotificationTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static IReadOnlyList<JsonElement> Completed(JsonElement page) =>
        page.Items().Where(n => n.GetProperty("type").GetString() == NotificationTypes.PurchaseReceiptCompleted).ToList();

    [Fact]
    public async Task The_owner_is_notified_when_a_receipt_is_completed_never_for_a_draft_and_with_atomic_parameters()
    {
        var world = await factory.WorldAsync();
        var managerAccount = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff, a => a.DisplayName = "Trần Văn Bình");
        await factory.GrantAsync(managerAccount, StaffRole.ZoneManager, world.Zone);
        var manager = await factory.LoginAsync(managerAccount.Username);

        var draft = await manager.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1.5m, 333), new LineBody(world.Seed, 2, 1_000)));
        Assert.Empty(Completed(await world.Owner.NotificationPageAsync()));
        Assert.False(await factory.QueryAsync(db => db.Notifications.AnyAsync(n => n.RelatedRecordId == draft.Id())));

        await (await manager.CompleteAsync(draft.Id())).ReadAsync(HttpStatusCode.OK);

        var notification = Assert.Single(Completed(await world.Owner.NotificationPageAsync()));
        var code = draft.GetProperty("code").GetString();
        var supplierName = await factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.Name).SingleAsync());
        Assert.Equal(("MSG-PURCHASE-01", "New purchase receipt", $"Trần Văn Bình completed purchase receipt {code} from {supplierName}: 2 lines, 2,500 VND."),
            (notification.GetProperty("messageCode").GetString(), notification.GetProperty("title").GetString(), notification.GetProperty("body").GetString()));
        var parameters = notification.GetProperty("messageParams");
        Assert.Equal(("Trần Văn Bình", code, supplierName, 2, 2_500m), (
            parameters.GetProperty("user_name").GetString(), parameters.GetProperty("receipt_code").GetString(), parameters.GetProperty("supplier_name").GetString(),
            parameters.GetProperty("line_count").GetInt32(), parameters.GetProperty("total_amount").GetDecimal()));
        Assert.Equal(("PurchaseReceipt", draft.Id(), code), (notification.GetProperty("relatedRecordType").GetString(),
            notification.GetProperty("relatedRecordId").GetGuid(), notification.GetProperty("relatedRecordCode").GetString()));
        Assert.False(notification.GetProperty("mandatory").GetBoolean());
        await (await world.Owner.OpenNotificationAsync(notification.Id())).ReadAsync(HttpStatusCode.OK);

        // The Zone Manager who completed it receives nothing; nor does the Accountant.
        Assert.Empty(Completed(await manager.NotificationPageAsync()));
        var push = Assert.Single(Pushes(await OwnerIdAsync(world)), p => p.MessageCode == "MSG-PURCHASE-01");
        Assert.Equal(code, push.MessageParams!.Value.GetProperty("receipt_code").GetString());
    }

    [Fact]
    public async Task The_owner_is_notified_also_when_the_owner_completed_the_receipt_and_may_turn_the_push_off()
    {
        var world = await factory.WorldAsync();
        var ownerId = await OwnerIdAsync(world);
        Assert.True((await world.Owner.OfferedTypesAsync()).TryGetValue(NotificationTypes.PurchaseReceiptCompleted, out var offered));
        Assert.Equal((false, true), offered);
        await (await world.Owner.SavePushAsync((NotificationTypes.PurchaseReceiptCompleted, false))).ReadAsync(HttpStatusCode.OK);

        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));

        // Kept in the list although its push is off (BR-ACCOUNT-12).
        Assert.Contains(Completed(await world.Owner.NotificationPageAsync()), n => n.GetProperty("relatedRecordId").GetGuid() == receipt.Id());
        Assert.DoesNotContain(Pushes(ownerId), p => p.MessageCode == "MSG-PURCHASE-01");
        // A staff account is not offered the type: only the Owner receives it.
        var manager = await factory.ZoneManagerAsync(world.Zone);
        Assert.DoesNotContain(NotificationTypes.PurchaseReceiptCompleted, (await manager.OfferedTypesAsync()).Keys);
        var accountant = await factory.AccountantAsync();
        Assert.Contains(NotificationTypes.PurchaseReceiptCancelledPaidInAdvance, (await accountant.OfferedTypesAsync()).Keys);
    }

    [Fact]
    public async Task A_removed_draft_or_cancelled_receipt_sends_nothing_more_and_its_notification_opens_as_no_longer_existing()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        var before = await factory.QueryAsync(db => db.Notifications.CountAsync());
        await (await world.Owner.CancelAsync(receipt.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));
        Assert.Equal(HttpStatusCode.NoContent, (await world.Owner.DeleteAsync($"/api/purchase-receipts/{draft.Id()}")).StatusCode);

        // The debt stays at zero after the cancellation, so no MSG-PURCHASE-08 either.
        Assert.Equal(before, await factory.QueryAsync(db => db.Notifications.CountAsync()));
        var notification = Assert.Single(Completed(await world.Owner.NotificationPageAsync()), n => n.GetProperty("relatedRecordId").GetGuid() == receipt.Id());
        // A cancelled receipt still exists and opens.
        await (await world.Owner.OpenNotificationAsync(notification.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.PurchaseReceipt.Cancelled, await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receipt.Id()).Select(r => r.StatusId).SingleAsync()));
    }

    // BR-PURCHASE-05, PM 03/11: the Owner who acts is told like any other Owner, in the list and by push. MSG-PURCHASE-08
    // goes out whenever the debt is below zero after the cancellation: when it goes below zero, when it was already, and
    // when a receipt worth nothing leaves it where it was.
    [Fact]
    public async Task The_owner_who_acts_is_notified_and_each_cancellation_leaving_the_debt_below_zero_sends_MSG_PURCHASE_08()
    {
        var world = await factory.WorldAsync();
        var ownerId = await OwnerIdAsync(world);
        var paid = await world.PaidAsync(100_000, new LineBody(world.Other, 1, 100_000));
        var unpaid = await world.CompletedAsync(new LineBody(world.Other, 1, 50_000));
        var free = await world.CompletedAsync(new LineBody(world.Other, 1, 0));
        var unrelated = await world.CompletedAsync(new LineBody(world.Other, 1, 200_000));

        foreach (var receipt in new[] { paid, unpaid, free, unrelated })
        {
            Assert.Single(await RowsToAsync(ownerId, receipt, NotificationTypes.PurchaseReceiptCompleted));
            Assert.Single(Pushes(ownerId), p => p.MessageCode == "MSG-PURCHASE-01" && CodeOf(p) == receipt.GetProperty("code").GetString());
        }

        // Debt 250,000 → 50,000: still above zero, so nothing is sent.
        await (await world.Owner.CancelAsync(unrelated.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);
        Assert.Empty(await RowsToAsync(ownerId, unrelated, NotificationTypes.PurchaseReceiptCancelledPaidInAdvance));

        // 50,000 → −50,000 goes below zero; −50,000 → −100,000 was already; −100,000 stays there with a receipt worth nothing.
        foreach (var (receipt, advance) in new[] { (paid, 50_000m), (unpaid, 100_000m), (free, 100_000m) })
        {
            await (await world.Owner.CancelAsync(receipt.Id(), "Wrong supplier")).ReadAsync(HttpStatusCode.OK);

            var row = Assert.Single(await RowsToAsync(ownerId, receipt, NotificationTypes.PurchaseReceiptCancelledPaidInAdvance));
            Assert.Equal("MSG-PURCHASE-08", row.MessageCode);
            Assert.Equal(advance, JsonDocument.Parse(row.MessageParams!).RootElement.GetProperty("advance_amount").GetDecimal());
            Assert.Single(Pushes(ownerId), p => p.MessageCode == "MSG-PURCHASE-08" && CodeOf(p) == receipt.GetProperty("code").GetString());
        }
    }

    private static string? CodeOf(FakeMessage push) => push.MessageParams!.Value.GetProperty("receipt_code").GetString();

    private Task<List<Domain.Entities.Platform.Notification>> RowsToAsync(Guid accountId, JsonElement receipt, string type) =>
        factory.QueryAsync(db => db.Notifications.Where(n => n.RecipientAccountId == accountId && n.RelatedRecordId == receipt.Id() && n.Type == type).ToListAsync());

    private Task<Guid> OwnerIdAsync(PurchasingWorld world) =>
        factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.CreatedBy!.Value).SingleAsync());

    private IReadOnlyList<FakeMessage> Pushes(Guid accountId) =>
        factory.Services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString());
}
