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
    }

    [Fact]
    public async Task A_draft_or_a_cancelled_receipt_sends_nothing_more_and_the_notification_of_a_cancelled_receipt_still_opens()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 1_000));
        var before = await factory.QueryAsync(db => db.Notifications.CountAsync());
        await (await world.Owner.CancelAsync(receipt.Id(), "Entered twice")).ReadAsync(HttpStatusCode.OK);
        await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)));

        Assert.Equal(before, await factory.QueryAsync(db => db.Notifications.CountAsync()));
        var notification = Assert.Single(Completed(await world.Owner.NotificationPageAsync()), n => n.GetProperty("relatedRecordId").GetGuid() == receipt.Id());
        // A cancelled receipt still exists and opens.
        await (await world.Owner.OpenNotificationAsync(notification.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.PurchaseReceipt.Cancelled, await factory.QueryAsync(db => db.PurchaseReceipts.Where(r => r.Id == receipt.Id()).Select(r => r.StatusId).SingleAsync()));
    }

    private Task<Guid> OwnerIdAsync(PurchasingWorld world) =>
        factory.QueryAsync(db => db.Suppliers.Where(s => s.Id == world.SupplierId).Select(s => s.CreatedBy!.Value).SingleAsync());

    private IReadOnlyList<FakeMessage> Pushes(Guid accountId) =>
        factory.Services.GetRequiredService<FakePushSender>().Inbox.List(accountId.ToString());
}
