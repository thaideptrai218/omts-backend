using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Completed receipts and Recorded returns are edited by anyone whose role edits receipts, with an audit; line discounts per
/// unit, the time received and the receiver; the zone is optional.
/// </summary>
public class PurchaseReceiptEditTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Receipts = "/api/purchase-receipts";

    private static LineBody Kept(JsonElement receipt, Guid itemId, decimal quantity, decimal unitPrice, decimal? discount = null) =>
        new(itemId, quantity, unitPrice, Discount: discount, LineId: receipt.LineOf(itemId));

    [Fact]
    public async Task Another_zone_manager_edits_a_completed_receipt_and_the_debt_moves_by_the_difference_with_an_audit()
    {
        var world = await factory.WorldAsync();
        var author = await factory.ZoneManagerAsync(world.Zone);
        var colleague = await factory.ZoneManagerAsync(world.Zone);
        var receipt = await author.CreateReceiptAsync(world.Body(new LineBody(world.Other, 10, 10_000), new LineBody(world.Material, 2, 5_000)) with { Complete = true });
        Assert.Equal(110_000m, await world.DebtAsync());

        var up = await (await colleague.EditAsync(receipt, world.Body(Kept(receipt, world.Other, 12, 10_000), Kept(receipt, world.Material, 2, 5_000))))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal((130_000m, "Completed"), (up.Money("total"), up.GetProperty("status").GetString()));
        Assert.Equal(130_000m, await world.DebtAsync());

        // Down, a line removed and one added: the debt follows the new total; the new line is numbered after every old one.
        var down = await (await colleague.EditAsync(up, world.Body(Kept(up, world.Other, 5, 10_000), new LineBody(world.Seed, 10, 1_000)) with { Discount = 5_000 }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal((60_000m, 55_000m), (down.Money("total"), down.Money("payable")));
        Assert.Equal(55_000m, await world.DebtAsync());
        Assert.Equal([1, 3], down.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("lineNumber").GetInt32()));
        Assert.Equal($"{receipt.GetProperty("code").GetString()}-3", down.SeedLotCodeOf(world.Seed));

        var audits = await factory.QueryAsync(db => db.AuditEvents.Where(e => e.RecordId == receipt.Id() && e.Action == "PurchaseReceipt.Edited").ToListAsync());
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.Contains("Lines", a.Before));
        Assert.Contains(audits, a => a.After!.Contains("130000") && a.Before!.Contains("110000"));

        // The supplier never changes; a Discount above the goods is refused.
        Assert.Equal("Supplier", await (await colleague.EditAsync(down, world.Body(Kept(down, world.Other, 5, 10_000)) with { SupplierId = await world.Owner.CreateSupplierAsync() }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Discount", await (await colleague.EditAsync(down, world.Body(Kept(down, world.Other, 1, 1_000)) with { Discount = 1_001 }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await (await colleague.PutAsJsonAsync($"{Receipts}/{receipt.Id()}", world.Body(Kept(down, world.Other, 5, 10_000))))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task A_seeded_or_returned_line_keeps_its_item_and_at_least_the_quantity_used()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 1_000), new LineBody(world.Other, 10, 2_000));
        await factory.SeedAsync(world.Raft.Id, receipt.LineOf(world.Seed), 30);
        await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Seed), 20);
        receipt = await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}/{receipt.Id()}");

        var below = await (await world.Owner.EditAsync(receipt, world.Body(Kept(receipt, world.Seed, 49, 1_000), Kept(receipt, world.Other, 10, 2_000))))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-10");
        Assert.Equal(("Line 1", 50m), (below.GetProperty("field").GetString(), below.Money("minQuantity")));
        await (await world.Owner.EditAsync(receipt, world.Body(Kept(receipt, world.Other, 10, 2_000)))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Line 1 item", await (await world.Owner.EditAsync(receipt, world.Body(
                new LineBody(world.Material, 100, 1_000, LineId: receipt.LineOf(world.Seed)), Kept(receipt, world.Other, 10, 2_000))))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        // Exactly seeded + returned is allowed, and a line nothing was taken from may go.
        var edited = await (await world.Owner.EditAsync(receipt, world.Body(Kept(receipt, world.Seed, 50, 1_200)))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((60_000m, 0m), (edited.Money("total"), edited.LineViewOf(world.Seed).Money("returnable")));
        // The return keeps the amount it was recorded with.
        Assert.Equal(20_000m, edited.Money("returned"));
        Assert.Equal(40_000m, await world.DebtAsync());
    }

    [Fact]
    public async Task A_line_discount_comes_off_each_unit_and_a_return_of_the_line_is_valued_at_the_net_price()
    {
        var world = await factory.WorldAsync();
        Assert.Equal("Line 1", await (await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 3, 10_000, Discount: 10_001)) with { Complete = true }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 1", await (await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 3, 10_000, Discount: 0.5m))))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var receipt = await world.CompletedAsync(new LineBody(world.Other, 3, 10_000, Discount: 500), new LineBody(world.Material, 1.5m, 333));
        Assert.Equal((500m, 28_500m), (receipt.LineViewOf(world.Other).Money("discount"), receipt.LineViewOf(world.Other).Money("lineAmount")));
        Assert.Equal((29_000m, 29_000m), (receipt.Money("total"), await world.DebtAsync()));
        var row = Assert.Single((await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}?supplierId={world.SupplierId}")).GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal(29_000m, row.Money("total"));

        var returned = await world.ReturnAsync(receipt.Id(), receipt.LineOf(world.Other), 1);
        Assert.Equal(9_500m, returned.Money("returned"));
        Assert.Equal(19_500m, await world.DebtAsync());

        var copy = await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}/{receipt.Id()}/copy");
        var copied = copy.GetProperty("receipt");
        Assert.Equal(500m, copied.GetProperty("lines")[0].Money("discount"));
        Assert.Equal(JsonValueKind.Null, copied.GetProperty("receivedById").ValueKind);
    }

    [Fact]
    public async Task The_time_received_and_the_receiver_are_kept_and_the_receivers_are_the_active_owner_and_staff()
    {
        var world = await factory.WorldAsync();
        var staff = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Staff);
        var trader = await factory.AddAccountAsync(NewCode(), Password, SubjectType.Trader);
        var receivers = (await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}/receivers")).EnumerateArray().ToList();
        Assert.Contains(receivers, r => r.Id() == staff.Id && r.GetProperty("name").GetString() == staff.DisplayName);
        Assert.DoesNotContain(receivers, r => r.Id() == trader.Id);
        var names = receivers.Select(r => r.GetProperty("name").GetString()!).ToList();
        Assert.Equal(names.Order(), names);

        var at = new DateTimeOffset(Today.AddDays(-2).ToDateTime(new TimeOnly(15, 30)), TimeSpan.FromHours(7));
        var body = world.Body(new LineBody(world.Other, 1, 1_000)) with { ReceiptDate = null, ReceivedAt = at.ToUniversalTime(), ReceivedById = staff.Id, Complete = true };
        var receipt = await world.Owner.CreateReceiptAsync(body);
        Assert.Equal((at, Today.AddDays(-2).ToString("yyyy-MM-dd")), (receipt.GetProperty("receivedAt").GetDateTimeOffset(), receipt.GetProperty("receiptDate").GetString()));
        Assert.Equal(staff.Id, receipt.GetProperty("receivedBy").Id());
        var row = Assert.Single((await world.Owner.GetFromJsonAsync<JsonElement>($"{Receipts}?supplierId={world.SupplierId}&from={Today.AddDays(-5):yyyy-MM-dd}"))
            .GetProperty("page").GetProperty("items").EnumerateArray());
        Assert.Equal((at, staff.DisplayName), (row.GetProperty("receivedAt").GetDateTimeOffset(), row.GetProperty("receivedByName").GetString()));

        // An edit that sends neither keeps both; a time in the future or a trader as receiver is refused.
        var kept = await (await world.Owner.EditAsync(receipt, world.Body(Kept(receipt, world.Other, 2, 1_000)) with { ReceiptDate = null })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((at, staff.Id), (kept.GetProperty("receivedAt").GetDateTimeOffset(), kept.GetProperty("receivedBy").Id()));
        Assert.Equal("Received at", await (await world.Owner.EditAsync(kept, world.Body(Kept(kept, world.Other, 2, 1_000)) with { ReceivedAt = DateTimeOffset.UtcNow.AddHours(1) }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Received by", await (await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 1_000)) with { ReceivedById = trader.Id }))
            .FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        // Without a receiver the caller took the goods in; without a zone the receipt still completes.
        var plain = await world.Owner.CreateReceiptAsync(new ReceiptBody { SupplierId = world.SupplierId, Lines = [new LineBody(world.Other, 1, 1_000)], Complete = false });
        var completed = await (await world.Owner.CompleteAsync(plain.Id())).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((JsonValueKind.Null, "Completed"), (completed.GetProperty("zone").ValueKind, completed.GetProperty("status").GetString()));
        Assert.Equal(receipt.GetProperty("recordedBy").Id(), completed.GetProperty("receivedBy").Id());
    }

    [Fact]
    public async Task A_recorded_return_is_edited_within_what_the_line_has_left_and_never_below_the_money_paid_back()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 10, 10_000));
        var line = receipt.LineOf(world.Other);
        await world.ReturnAsync(receipt.Id(), line, 2);
        var saved = await (await world.Owner.PostAsJsonAsync("/api/purchase-returns", new
        {
            receiptId = receipt.Id(), reason = "Broken", complete = true, refund = new { amount = 20_000, method = "Cash" },
            lines = new[] { new { receiptLineId = line, quantity = 3m } },
        })).ReadAsync(HttpStatusCode.Created);
        var debtBefore = await world.DebtAsync();
        var colleague = await factory.ZoneManagerAsync(world.Zone);

        object Edit(decimal quantity, decimal discount = 0, string reason = "Broken in transport") => new
        {
            receiptId = receipt.Id(), reason, discount, rowVersion = saved.GetProperty("rowVersion").GetString(),
            lines = new[] { new { receiptLineId = line, quantity } },
        };

        // 10 received, 2 on the other return: at most 8.
        var over = await (await colleague.PutAsJsonAsync($"/api/purchase-returns/{saved.Id()}", Edit(9))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.Equal(8m, over.Money("maxQuantity"));
        // 20,000 was paid back: one unit (10,000) is too little, two less a discount of 1 is too.
        await (await colleague.PutAsJsonAsync($"/api/purchase-returns/{saved.Id()}", Edit(1))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
        Assert.Equal("Refund", await (await colleague.PutAsJsonAsync($"/api/purchase-returns/{saved.Id()}", Edit(2, discount: 1))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var edited = await (await colleague.PutAsJsonAsync($"/api/purchase-returns/{saved.Id()}", Edit(8))).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((80_000m, "Broken in transport", "Recorded"), (edited.Money("netValue"), edited.GetProperty("reason").GetString(), edited.GetProperty("status").GetString()));
        Assert.Single(edited.GetProperty("refunds").EnumerateArray());
        // The return now lowers the debt by 80,000 instead of 30,000.
        Assert.Equal(debtBefore - 50_000m, await world.DebtAsync());
        Assert.Contains("PurchaseReturn.Edited", await factory.AuditActionsAsync(saved.Id()));
    }

    [Fact]
    public async Task A_recorded_return_without_a_receipt_changes_its_lines_and_prices()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 10_000));
        var saved = await (await world.Owner.PostAsJsonAsync("/api/purchase-returns", new
        {
            supplierId = world.SupplierId, reason = "Damaged", complete = true,
            lines = new[] { new { itemId = world.Other, unitPrice = 10_000m, quantity = 2m } },
        })).ReadAsync(HttpStatusCode.Created);
        // Recorded without a zone: it is optional.
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("zone").ValueKind);

        var edited = await (await world.Owner.PutAsJsonAsync($"/api/purchase-returns/{saved.Id()}", new
        {
            supplierId = world.SupplierId, reason = "Damaged", rowVersion = saved.GetProperty("rowVersion").GetString(),
            lines = new[] { new { itemId = world.Other, unitPrice = 9_000m, quantity = 1m }, new { itemId = world.Material, unitPrice = 5_000m, quantity = 2m } },
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((19_000m, 2), (edited.Money("returnValue"), edited.GetProperty("lines").GetArrayLength()));
        Assert.Equal(81_000m, await world.DebtAsync());
    }
}
