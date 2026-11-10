using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// The price and time of a return (the price defaults to what one unit was paid, the receipt discount prorated), and the payment
/// voucher: its time and payer, the receipts it pays, and the edit of its header that never changes its money.
/// </summary>
public class PurchaseReturnPriceAndPaymentVoucherTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Returns = "/api/purchase-returns";
    private const string Payments = "/api/supplier-payments";

    private static object ReturnOf(JsonElement receipt, Guid itemId, decimal quantity, decimal? unitPrice = null, DateTimeOffset? returnedAt = null) => new
    {
        receiptId = receipt.Id(), reason = "Weak seed", complete = true, returnedAt,
        lines = new[] { new { receiptLineId = receipt.LineOf(itemId), quantity, unitPrice } },
    };

    // The user's PN000027: 204 × 3,000 with 60,300 off the receipt is 551,700, so a unit was paid 2,704 (2,704.4).
    [Fact]
    public async Task A_return_defaults_to_what_one_unit_was_paid_with_the_receipt_discount_prorated()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 204, 3_000)) with { Discount = 60_300, Complete = true });
        Assert.Equal(2_704, receipt.LineViewOf(world.Other).Money("netUnitPrice"));

        var saved = await (await world.Owner.PostAsJsonAsync(Returns, ReturnOf(receipt, world.Other, 200))).ReadAsync(HttpStatusCode.Created);

        var line = Assert.Single(saved.GetProperty("lines").EnumerateArray());
        Assert.Equal((2_704m, 540_800m), (line.Money("unitPrice"), line.Money("returnValue")));
        Assert.Equal(551_700 - 540_800, await world.DebtAsync());
    }

    [Fact]
    public async Task An_entered_return_price_is_kept_and_an_edit_may_change_it_or_keep_it()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 10, 3_000)) with { Discount = 3_000, Complete = true });

        var saved = await (await world.Owner.PostAsJsonAsync(Returns, ReturnOf(receipt, world.Other, 4, unitPrice: 3_000))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(12_000, saved.Money("returnValue"));

        var edited = await (await world.Owner.PutAsJsonAsync($"{Returns}/{saved.Id()}", new
        {
            reason = "Weak seed", rowVersion = saved.GetProperty("rowVersion").GetString(),
            lines = new[] { new { receiptLineId = receipt.LineOf(world.Other), quantity = 5m, unitPrice = (decimal?)2_500 } },
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(12_500, edited.Money("returnValue"));

        var kept = await (await world.Owner.PutAsJsonAsync($"{Returns}/{saved.Id()}", new
        {
            reason = "Weak seed", rowVersion = edited.GetProperty("rowVersion").GetString(),
            lines = new[] { new { receiptLineId = receipt.LineOf(world.Other), quantity = 2m, unitPrice = (decimal?)null } },
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(5_000, kept.Money("returnValue"));

        var refused = await world.Owner.PostAsJsonAsync(Returns, ReturnOf(receipt, world.Other, 1, unitPrice: 10.5m));
        Assert.Equal("Line 1", await refused.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_time_returned_sets_the_return_date_is_never_in_the_future_and_may_be_edited()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 10, 1_000)) with { ReceiptDate = Today.AddDays(-5), Complete = true });
        var twoDaysAgo = new DateTimeOffset(Today.AddDays(-2).ToDateTime(new TimeOnly(15, 30)), TimeSpan.FromHours(7));

        var future = await world.Owner.PostAsJsonAsync(Returns, ReturnOf(receipt, world.Other, 1, returnedAt: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("Returned at", await future.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var saved = await (await world.Owner.PostAsJsonAsync(Returns, ReturnOf(receipt, world.Other, 1, returnedAt: twoDaysAgo))).ReadAsync(HttpStatusCode.Created);
        Assert.Equal(Today.AddDays(-2).ToString("yyyy-MM-dd"), saved.GetProperty("returnDate").GetString());
        Assert.Equal(twoDaysAgo, saved.GetProperty("returnedAt").GetDateTimeOffset());

        var yesterday = twoDaysAgo.AddDays(1);
        var edited = await (await world.Owner.PutAsJsonAsync($"{Returns}/{saved.Id()}", new
        {
            reason = "Weak seed", rowVersion = saved.GetProperty("rowVersion").GetString(), returnedAt = yesterday,
            lines = new[] { new { receiptLineId = receipt.LineOf(world.Other), quantity = 1m } },
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((Today.AddDays(-1).ToString("yyyy-MM-dd"), yesterday), (edited.GetProperty("returnDate").GetString(), edited.GetProperty("returnedAt").GetDateTimeOffset()));

        var detail = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal(yesterday, Assert.Single(detail.GetProperty("returns").EnumerateArray()).GetProperty("returnedAt").GetDateTimeOffset());
        Assert.Contains("PurchaseReturn.Edited", await factory.AuditActionsAsync(saved.Id()));
    }

    private static object PaymentOf(PurchasingWorld world, decimal amount, object[]? allocations = null, DateTimeOffset? paidAt = null, Guid? paidById = null) => new
    {
        supplierId = world.SupplierId, kind = "Payment", amount, method = "Cash", paymentDate = paidAt is null ? Today : (DateOnly?)null,
        paidAt, paidById, allocations,
    };

    [Fact]
    public async Task A_payment_set_against_a_newer_receipt_pays_it_and_the_debt_is_unchanged()
    {
        var world = await factory.WorldAsync();
        var older = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { ReceiptDate = Today.AddDays(-9), Complete = true });
        var newer = await world.CompletedAsync(new LineBody(world.Other, 1, 50_000));

        var tooMuch = await world.Owner.PostAsJsonAsync(Payments, PaymentOf(world, 60_000, [new { receiptId = newer.Id(), amount = 50_001 }]));
        Assert.Equal("Allocation 1", await tooMuch.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var aboveAmount = await world.Owner.PostAsJsonAsync(Payments, PaymentOf(world, 40_000, [new { receiptId = newer.Id(), amount = 50_000 }]));
        Assert.Equal("Allocations", await aboveAmount.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var paid = await (await world.Owner.PostAsJsonAsync(Payments, PaymentOf(world, 50_000, [new { receiptId = newer.Id(), amount = 50_000 }])))
            .ReadAsync(HttpStatusCode.Created);

        var part = Assert.Single(paid.GetProperty("allocations").EnumerateArray());
        Assert.Equal((newer.Id(), newer.GetProperty("code").GetString(), 50_000m, 0m, 50_000m, 0m), (
            part.GetProperty("receiptId").GetGuid(), part.GetProperty("receiptCode").GetString(), part.Money("receiptPayable"),
            part.Money("paidBefore"), part.Money("amount"), part.Money("receiptRemaining")));
        Assert.Equal(100_000, await world.DebtAsync());
        var debt = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}");
        var unpaid = Assert.Single(debt.GetProperty("unpaidReceipts").EnumerateArray());
        Assert.Equal((older.Id(), 100_000m), (unpaid.Id(), unpaid.Money("remaining")));
        Assert.Equal(older.GetProperty("receivedAt").GetDateTimeOffset(), unpaid.GetProperty("receivedAt").GetDateTimeOffset());
        Assert.Equal(50_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{newer.Id()}")).Money("paid"));
        Assert.Equal(0, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{older.Id()}")).Money("paid"));
        var payments = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{newer.Id()}/payments");
        Assert.Equal(paid.Id(), Assert.Single(payments.EnumerateArray()).Id());
    }

    [Fact]
    public async Task A_payment_without_parts_still_covers_the_oldest_receipt_and_names_its_payer_and_time()
    {
        var world = await factory.WorldAsync();
        var older = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 1, 100_000)) with { ReceiptDate = Today.AddDays(-9), Complete = true });
        await world.CompletedAsync(new LineBody(world.Other, 1, 50_000));
        var payer = (await world.Owner.GetFromJsonAsync<JsonElement>($"{Payments}/payers")).EnumerateArray().First();
        var paidAt = new DateTimeOffset(Today.AddDays(-1).ToDateTime(new TimeOnly(8, 15)), TimeSpan.FromHours(7));

        var paid = await (await world.Owner.PostAsJsonAsync(Payments, PaymentOf(world, 50_000, paidAt: paidAt, paidById: payer.Id()))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal((paidAt, Today.AddDays(-1).ToString("yyyy-MM-dd")), (paid.GetProperty("paidAt").GetDateTimeOffset(), paid.GetProperty("paymentDate").GetString()));
        Assert.Equal(payer.Id(), paid.GetProperty("paidBy").Id());
        Assert.Equal(0, paid.GetProperty("allocations").GetArrayLength());
        Assert.Equal(50_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{older.Id()}")).Money("paid"));
        Assert.Equal(100_000, await world.DebtAsync());
        var future = await world.Owner.PostAsJsonAsync(Payments, PaymentOf(world, 1_000, paidAt: DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("Paid at", await future.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_voucher_header_may_be_edited_but_never_its_money()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1, 80_000));
        var paid = await (await world.Owner.PostAsJsonAsync(Payments, PaymentOf(world, 30_000, [new { receiptId = receipt.Id(), amount = 30_000 }])))
            .ReadAsync(HttpStatusCode.Created);
        var rowVersion = paid.GetProperty("rowVersion").GetString();
        var paidAt = new DateTimeOffset(Today.AddDays(-3).ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(7));

        var moneyChanged = await world.Owner.PutAsJsonAsync($"{Payments}/{paid.Id()}", new { rowVersion, method = "Cash", amount = 31_000 });
        Assert.Equal("Amount", await moneyChanged.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var partsChanged = await world.Owner.PutAsJsonAsync($"{Payments}/{paid.Id()}", new
        {
            rowVersion, method = "Cash", allocations = new[] { new { receiptId = receipt.Id(), amount = 20_000 } },
        });
        Assert.Equal("Allocations", await partsChanged.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var noVersion = await world.Owner.PutAsJsonAsync($"{Payments}/{paid.Id()}", new { method = "Cash" });
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);

        var edited = await (await world.Owner.PutAsJsonAsync($"{Payments}/{paid.Id()}", new
        {
            rowVersion, method = "BankTransfer", bankReference = $"FT-{Guid.NewGuid():N}", note = "Đợt 1", paidAt,
            amount = 30_000, kind = "Payment", allocations = new[] { new { receiptId = receipt.Id(), amount = 30_000 } },
        })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((30_000m, "BankTransfer", "Đợt 1"), (edited.Money("amount"), edited.GetProperty("method").GetString(), edited.GetProperty("note").GetString()));
        Assert.Equal((paidAt, Today.AddDays(-3).ToString("yyyy-MM-dd")), (edited.GetProperty("paidAt").GetDateTimeOffset(), edited.GetProperty("paymentDate").GetString()));
        Assert.Equal(30_000, Assert.Single(edited.GetProperty("allocations").EnumerateArray()).Money("amount"));
        Assert.Equal(50_000, await world.DebtAsync());
        Assert.Contains("SupplierPayment.Edited", await factory.AuditActionsAsync(paid.Id()));

        await (await world.Owner.CancelPaymentAsync(paid.Id(), "Wrong")).ReadAsync(HttpStatusCode.OK);
        var current = (await world.Owner.GetFromJsonAsync<JsonElement>($"{Payments}/{paid.Id()}")).GetProperty("rowVersion").GetString();
        var cancelled = await world.Owner.PutAsJsonAsync($"{Payments}/{paid.Id()}", new { rowVersion = current, method = "Cash" });
        await cancelled.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }
}
