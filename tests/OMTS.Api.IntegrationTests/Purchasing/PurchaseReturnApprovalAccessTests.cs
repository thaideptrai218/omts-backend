using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Application.Features.Notifications;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-02 step 4, §I.4.4: a Zone Manager confirms the Owner's approval in person (MSG-PURCHASE-02, enforced by the API), the
/// Owner is not asked; any Zone Manager returns, whatever the zone (BR-FARM-08); the Accountant only reads; nobody is notified.
/// </summary>
public class PurchaseReturnApprovalAccessTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static ReturnBody Return(Guid lineId, decimal quantity) => new() { Lines = [new ReturnLineBody(lineId, quantity)] };

    private Task<long?> LastCodeNumberAsync() =>
        factory.QueryAsync(db => db.DocumentSequences.Where(s => s.Key == "TH").Select(s => (long?)s.LastValue).SingleOrDefaultAsync());

    [Fact]
    public async Task A_zone_manager_must_confirm_the_owners_approval_and_nothing_is_saved_before_it()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        var supplierName = receipt.GetProperty("supplier").GetProperty("name").GetString()!;
        var numberBefore = await LastCodeNumberAsync();

        var problem = await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1))).ProblemAsync(HttpStatusCode.Conflict, "MSG-PURCHASE-02");

        Assert.Equal(("ownerApproved", 25_000m, supplierName), (problem.GetProperty("confirmFlag").GetString(), problem.Money("returnAmount"), problem.GetProperty("supplierName").GetString()));
        Assert.Equal($"Has the Cooperative Owner approved this return of 25,000 VND to {supplierName} in person?", problem.GetProperty("detail").GetString());
        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));
        Assert.Equal(100_000, await world.DebtAsync());

        // Answered Yes with the same request: saved, and the audit keeps the answer.
        var saved = await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1) with { OwnerApproved = true })).ReadAsync(HttpStatusCode.Created);
        var returnId = saved.GetProperty("returns")[0].Id();
        Assert.Contains("\"OwnerApprovalConfirmed\":true", await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == returnId).Select(e => e.After!).SingleAsync()));
        // The refused request spent no number of the TH sequence.
        Assert.Equal((numberBefore ?? 0) + 1, await LastCodeNumberAsync());
    }

    // Step 3 comes before step 4: a return that could not be saved is not put to the Owner's approval, even with the flag.
    [Fact]
    public async Task The_fields_and_quantities_are_checked_before_the_approval_is_asked_and_the_flag_skips_no_check()
    {
        var world = await factory.WorldAsync();
        var manager = await factory.ZoneManagerAsync(world.Zone);
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);

        foreach (var approved in new[] { false, true })
        {
            Assert.Equal("Reason", await (await manager.PostReturnAsync(receipt.Id(), Return(line, 1) with { Reason = " ", OwnerApproved = approved })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
            await (await manager.PostReturnAsync(receipt.Id(), Return(line, 5) with { OwnerApproved = approved })).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));
    }

    [Fact]
    public async Task The_owner_is_not_asked_for_the_approval_and_the_audit_says_so()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));

        var saved = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);

        var returnId = saved.GetProperty("returns")[0].Id();
        Assert.Contains("\"OwnerApprovalConfirmed\":false", await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == returnId).Select(e => e.After!).SingleAsync()));
    }

    // BR-PURCHASE-05, BR-PURCHASE-07: a return sends no notification, to anybody.
    [Fact]
    public async Task A_return_and_its_cancellation_notify_nobody_also_when_the_debt_stays_below_zero()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.PaidAsync(200_000, new LineBody(world.Other, 4, 25_000));
        var notifications = await NotificationCountAsync();

        var saved = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);
        var cancelled = await (await world.Owner.CancelReturnAsync(saved.GetProperty("returns")[0].Id(), "Not sent back")).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Cancelled", cancelled.GetProperty("returns")[0].GetProperty("status").GetString());
        Assert.Equal(-100_000, await world.DebtAsync());
        Assert.Equal(notifications, await NotificationCountAsync());
        Assert.Equal(0, await factory.QueryAsync(db => db.Notifications.CountAsync(n => n.RelatedRecordId == receipt.Id() && n.Type == NotificationTypes.PurchaseReceiptCancelledPaidInAdvance)));
    }

    [Fact]
    public async Task Returns_follow_the_role_only_any_zone_manager_creates_and_reads_them_and_the_accountant_only_reads()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 9_000));
        var line = receipt.LineOf(world.Seed);
        var created = await world.ReturnAsync(receipt.Id(), line, 1);
        var returnId = created.GetProperty("returns")[0].Id();
        var (otherZone, _) = await factory.ZoneWithRaftAsync();

        // Receipts and returns have no zone (BR-FARM-08): a Zone Manager of another zone reads and returns too.
        var otherManager = await factory.ZoneManagerAsync(otherZone);
        await (await otherManager.GetAsync($"/api/purchase-returns/{returnId}")).ReadAsync(HttpStatusCode.OK);
        await (await otherManager.PostReturnAsync(receipt.Id(), Return(line, 1) with { OwnerApproved = true })).ReadAsync(HttpStatusCode.Created);
        // The Accountant views receipts and returns but creates none (§I.4.4 PURCHASE-02 No, PURCHASE-03 R).
        var viewer = await factory.AccountantAsync(world.Zone);
        await (await viewer.PostReturnAsync(receipt.Id(), Return(line, 1) with { OwnerApproved = true })).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        await (await viewer.GetAsync($"/api/purchase-returns/{returnId}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(2, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));
    }

    private Task<int> NotificationCountAsync() => factory.QueryAsync(db => db.Notifications.CountAsync());
}
