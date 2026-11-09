using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>Draft returns count nowhere; returns without a receipt, discounts and money paid back follow the debt formulas.</summary>
public class PurchaseReturnDocumentTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<JsonElement> SaveAsync(PurchasingWorld world, object body) =>
        await (await world.Owner.PostAsJsonAsync("/api/purchase-returns", body)).ReadAsync(HttpStatusCode.Created);

    private static object DraftOf(PurchasingWorld world, Guid receiptId, Guid lineId, decimal quantity, bool complete = false) => new
    {
        receiptId, zoneId = world.Zone.Id, reason = complete ? "Weak seed" : null, complete,
        lines = new[] { new { receiptLineId = (Guid?)lineId, quantity } },
    };

    private static object Standalone(PurchasingWorld world, decimal quantity, decimal unitPrice, decimal discount = 0, object? refund = null, bool complete = true) => new
    {
        supplierId = world.SupplierId, zoneId = world.Zone.Id, reason = "Damaged", discount, complete, refund,
        lines = new[] { new { itemId = (Guid?)world.Other, unitPrice = (decimal?)unitPrice, quantity } },
    };

    [Fact]
    public async Task A_draft_return_lowers_no_debt_holds_no_quantity_and_does_not_block_the_cancel()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);

        var draft = await SaveAsync(world, DraftOf(world, receipt.Id(), line, 4));

        Assert.Equal(StatusIds.PurchaseReturn.Draft, draft.GetProperty("statusId").GetInt32());
        Assert.Equal(100_000, await world.DebtAsync());
        var read = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal(4m, read.LineViewOf(world.Other).Money("returnable"));
        Assert.Equal(0, read.GetProperty("returns").GetArrayLength());
        Assert.Equal(0m, read.Money("returned"));
        var statement = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}/statement");
        Assert.DoesNotContain(statement.GetProperty("entries").EnumerateArray(), e => e.GetProperty("kind").GetString() == "Return");
        var preview = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}/cancel-preview");
        Assert.True(preview.GetProperty("canCancel").GetBoolean());
        await (await world.Owner.CancelAsync(receipt.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_return_without_a_receipt_lowers_the_debt_by_its_net_value_and_shows_no_receipt_code()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));

        var saved = await SaveAsync(world, Standalone(world, 2, 50_000, discount: 10_000));

        Assert.Equal(JsonValueKind.Null, saved.GetProperty("receipt").ValueKind);
        Assert.Equal(100_000m, saved.Money("returnValue"));
        Assert.Equal(90_000m, saved.Money("netValue"));
        Assert.Equal(910_000, await world.DebtAsync());
        var statement = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}/statement");
        var entry = statement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Return");
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("receiptCode").ValueKind);
        Assert.Equal(-90_000m, entry.Money("amount"));

        var cancelled = await (await world.Owner.CancelReturnAsync(saved.Id(), "Mistake")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(StatusIds.PurchaseReturn.Cancelled, cancelled.GetProperty("statusId").GetInt32());
        Assert.Equal(1_000_000, await world.DebtAsync());
    }

    [Fact]
    public async Task Discounts_of_receipts_and_payments_lower_the_debt_and_all_discount_payments_are_allowed()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 10, 100_000)) with { Complete = true, Discount = 50_000 });
        Assert.Equal(950_000m, receipt.Money("payable"));
        Assert.Equal(950_000, await world.DebtAsync());

        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 100_000, DiscountAmount = 20_000 })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(830_000, await world.DebtAsync());
        await (await world.Owner.PostPaymentAsync(new PaymentBody { SupplierId = world.SupplierId, Amount = 0, DiscountAmount = 30_000 })).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(800_000, await world.DebtAsync());

        var payments = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}/payments");
        Assert.Equal(150_000m, payments.EnumerateArray().Sum(p => p.Money("applied")));
        var listed = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?Search={receipt.GetProperty("code").GetString()}");
        Assert.Equal(150_000m, listed.GetProperty("page").GetProperty("items")[0].Money("paid"));
    }

    [Fact]
    public async Task A_discount_above_the_total_cannot_be_completed()
    {
        var world = await factory.WorldAsync();
        var refused = await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 100)) with { Complete = true, Discount = 101 });
        Assert.Equal("Discount", await refused.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_return_from_a_receipt_keeps_the_quantity_limit_when_completed()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        await world.ReturnAsync(receipt.Id(), line, 3);

        var draft = await SaveAsync(world, DraftOf(world, receipt.Id(), line, 2));
        var refused = await world.Owner.PostAsJsonAsync($"/api/purchase-returns/{draft.Id()}/complete", new { reason = "x" });
        await refused.ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        var edited = await (await world.Owner.PutAsJsonAsync($"/api/purchase-returns/{draft.Id()}", new
        {
            receiptId = receipt.Id(), zoneId = world.Zone.Id, reason = "Weak seed", rowVersion = draft.GetProperty("rowVersion").GetString(),
            lines = new[] { new { receiptLineId = (Guid?)line, quantity = 2m } },
        })).ReadAsync(HttpStatusCode.OK);
        await (await world.Owner.PostAsJsonAsync($"/api/purchase-returns/{edited.Id()}/complete", new { }))
            .ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
    }

    [Fact]
    public async Task Money_the_supplier_gives_back_on_completion_is_a_linked_payment_at_most_the_net_value()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));

        var tooMuch = await world.Owner.PostAsJsonAsync("/api/purchase-returns", Standalone(world, 1, 50_000, refund: new { amount = 50_001, method = "Cash" }));
        Assert.Equal("Refund amount", await tooMuch.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var saved = await SaveAsync(world, Standalone(world, 1, 50_000, refund: new { amount = 20_000, method = "Cash" }));

        Assert.Equal(20_000m, saved.GetProperty("refunds")[0].Money("amount"));
        // Goods go back at 50,000 and 20,000 of it comes back as money.
        Assert.Equal(970_000, await world.DebtAsync());
    }

    [Fact]
    public async Task A_draft_is_removed_and_only_by_who_recorded_it()
    {
        var world = await factory.WorldAsync();
        var draft = await SaveAsync(world, Standalone(world, 1, 1_000, complete: false));
        var manager = await factory.ZoneManagerAsync(world.Zone);
        await (await manager.DeleteAsync($"/api/purchase-returns/{draft.Id()}")).ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(HttpStatusCode.NoContent, (await world.Owner.DeleteAsync($"/api/purchase-returns/{draft.Id()}")).StatusCode);
    }

    [Fact]
    public async Task The_list_totals_recorded_returns_only()
    {
        var world = await factory.WorldAsync();
        await world.CompletedAsync(new LineBody(world.Other, 10, 100_000));
        await SaveAsync(world, Standalone(world, 1, 40_000));
        await SaveAsync(world, Standalone(world, 1, 70_000, complete: false));

        var list = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-returns?SupplierId={world.SupplierId}&SortBy=total&SortDir=desc");

        Assert.Equal(40_000m, list.Money("totalValue"));
        Assert.Equal(2, list.GetProperty("page").GetProperty("items").GetArrayLength());
    }
}
