using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The edit of an invoice like a purchase receipt (PUT {id}): who may, the product ledger (only a changed line writes rows, a removed line leaves none of its own, a price cut and a cancellation), the debt by the
/// difference, the advance of the order that an invoice can no longer hold, the code and the progress of the order.
/// </summary>
public class SalesInvoiceEditTests
{
    private static Task<List<(ProductStockMovementKind Kind, decimal Kg)>> StockRowsAsync(AuthApiFactory api, Guid productId) =>
        api.QueryAsync(async db => (await db.ProductStockMovements.AsNoTracking().Where(m => m.ProductId == productId).OrderBy(m => m.RecordedAt).ToListAsync())
            .Select(m => (m.Kind, m.QuantityKg)).ToList());

    /// <summary>What the sales took off the stock so far: the Sale rows less the ones that were reversed.</summary>
    private static decimal SoldKg(List<(ProductStockMovementKind Kind, decimal Kg)> rows) => rows.Where(r => r.Kind != ProductStockMovementKind.Harvest).Sum(r => r.Kg);

    /// <summary>A lot split per product: the kg of the sale leave the stock of the product on that lot, so the ledger has rows to follow.</summary>
    private static async Task<Guid> SplitLotAsync(SaleSetup sale) => (await sale.Owner.Client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 8, 400m)))).Id();

    private static Dictionary<string, object?> KgLine(SaleSetup sale, Guid lotId, decimal kg, Guid? orderLineId = null, decimal? price = null) => new()
    {
        ["productId"] = sale.ProductId, ["harvestLotId"] = lotId, ["kg"] = kg, ["salesOrderLineId"] = orderLineId, ["unitPrice"] = price,
    };

    private static async Task<(Guid OrderId, Guid LineId)> OrderWithAdvanceAsync(SaleSetup sale, decimal kg, decimal advance)
    {
        var owner = sale.Owner.Client;
        var body = OrderBody(sale.CustomerId, sale.ProductId, await owner.BaseUnitIdAsync(sale.ProductId), quantity: kg).WithField("advance", new { amount = advance, method = "Cash" });
        var order = (await owner.CreateOrderAsync(body)).GetProperty("order");
        return (order.Id(), order.GetProperty("lines")[0].Id());
    }

    [Fact]
    public async Task Only_a_caller_with_the_edit_right_edits_and_only_one_with_the_delete_right_cancels()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var sold = await sale.Manager.Client.SoldAsync(sale, sale.LotId);
        var accountant = await api.StaffAsync(await api.ZoneEntityAsync(sale.ZoneId), StaffRole.Accountant);
        // 300,000 were paid in cash: an invoice of 150,000 leaves 150,000 owed back, which is confirmed.
        var edit = Edit(sold, [Line(sale.ProductId, sale.LotId, 5m)], sale.LotId).With("confirmOwedBack", true);

        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.EditAsync(sold.Id(), edit)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await accountant.Client.EditAsync(sold.Id(), edit)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).ErrorCodeAsync());
        Assert.DoesNotContain("Edit", (await sale.Manager.Client.InvoiceAsync(sold.Id())).Actions());

        var edited = (await sale.Owner.Client.EditedAsync(sold.Id(), edit)).GetProperty("invoice");
        Assert.Equal(150_000m, edited.Money("total"));
        // A Cancelled invoice is not edited (a stale version answers MSG06 on SQL Server: the in-memory provider does not enforce row versions).
        await sale.Owner.Client.CancelledAsync(sold.Id(), Cancellation(edited).With("cashGivenBack", GivenBack((await api.PaymentsOfAsync(sold.Id())).Single().Id, true)));
        Assert.Equal("MSG-GEN-09", await (await sale.Owner.Client.EditAsync(sold.Id(), Edit(await sale.Owner.Client.InvoiceAsync(sold.Id()), [Line(sale.ProductId, sale.LotId, 5m)], sale.LotId))).ErrorCodeAsync());
    }

    // A line that changed is posted again, an unchanged one writes nothing, a removed line leaves no row of its own and an added line posts one.
    [Fact]
    public async Task The_product_ledger_follows_an_edit_without_churn_and_a_removed_line_posts_no_row_of_its_own()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var second = (await owner.CreateProductAsync(unitPrice: 40_000m)).Id();
        var lot = (await owner.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 8, 400m), (second, 4, 100m)))).Id();
        Dictionary<string, object?> Other(decimal kg) => new() { ["productId"] = second, ["harvestLotId"] = lot, ["kg"] = kg };
        var sold = (await owner.SellAsync(Invoice(sale.CustomerId, [KgLine(sale, lot, 5m), Other(3m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lot))).GetProperty("invoice");
        Assert.Equal([(ProductStockMovementKind.Harvest, 400m), (ProductStockMovementKind.Sale, -5m)], await StockRowsAsync(api, sale.ProductId));

        // The same lines again: no row is written.
        var same = (await owner.EditedAsync(sold.Id(), Edit(sold, [KgLine(sale, lot, 5m), Other(3m)], lot))).GetProperty("invoice");
        Assert.Equal(2, (await StockRowsAsync(api, sale.ProductId)).Count);
        Assert.Equal(2, (await StockRowsAsync(api, second)).Count);

        // The second line is removed: the Sale rows of the invoice are reversed and only the kept line is posted again, so nothing posts the removed line.
        var shorter = (await owner.EditedAsync(sold.Id(), Edit(same, [KgLine(sale, lot, 5m)], lot))).GetProperty("invoice");
        Assert.Equal([(ProductStockMovementKind.Harvest, 100m), (ProductStockMovementKind.Sale, -3m), (ProductStockMovementKind.Reversal, 3m)], await StockRowsAsync(api, second));
        Assert.Equal(-5m, SoldKg(await StockRowsAsync(api, sale.ProductId)));
        Assert.Equal(1, shorter.GetProperty("lines").GetArrayLength());

        // A line added posts a Sale row for its product, and a change of kg is reversed and posted again.
        var longer = (await owner.EditedAsync(sold.Id(), Edit(shorter, [KgLine(sale, lot, 6m), Other(2m)], lot))).GetProperty("invoice");
        Assert.Equal((-6m, -2m), (SoldKg(await StockRowsAsync(api, sale.ProductId)), SoldKg(await StockRowsAsync(api, second))));
        Assert.Equal(2, longer.GetProperty("lines").GetArrayLength());

        // A price cut changes the money only; a cancellation reverses what is left.
        var rows = (await StockRowsAsync(api, sale.ProductId)).Count + (await StockRowsAsync(api, second)).Count;
        await owner.CorrectedAsync(sold.Id(), PriceCut(longer, 1_000m));
        Assert.Equal(rows, (await StockRowsAsync(api, sale.ProductId)).Count + (await StockRowsAsync(api, second)).Count);
        await owner.CancelledAsync(sold.Id(), Cancellation(await owner.InvoiceAsync(sold.Id())));
        Assert.Equal((0m, 0m), (SoldKg(await StockRowsAsync(api, sale.ProductId)), SoldKg(await StockRowsAsync(api, second))));
    }

    [Fact]
    public async Task A_total_under_what_was_paid_asks_about_the_money_owed_back_and_the_note_and_code_change_with_the_edit()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var sold = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId))).GetProperty("invoice");
        var other = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m), sale.LotId).With("code", "KEEP-1"))).GetProperty("invoice");
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));

        var body = Edit(sold, [Line(sale.ProductId, sale.LotId, 8m)], sale.LotId);
        var question = await (await owner.EditAsync(sold.Id(), body)).QuestionAsync();
        Assert.Equal(("MSG-SALE-17", "confirmOwedBack", 60_000m), (question.Code, question.Field, question.Body.Money("owedBackAmount")));
        Assert.Equal(300_000m, (await owner.InvoiceAsync(sold.Id())).Money("total"));

        var taken = await (await owner.EditAsync(sold.Id(), body.With("confirmOwedBack", true).With("code", "keep-1"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-02", other.Id()), (taken.Text("code"), taken.GetProperty("existingId").GetGuid()));

        var edited = (await owner.EditedAsync(sold.Id(), body.With("code", "EDIT-2").With("note", "Weighed again"))).GetProperty("invoice");
        Assert.Equal(("EDIT-2", "Weighed again", 240_000m), (edited.Text("code"), edited.Text("note"), edited.Money("total")));
        Assert.Equal(-60_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        Assert.Equal("MSG-SALE-18", (await api.OwedBackNoticesAsync(sold.Id()))[0].MessageCode);
    }

    // An invoice that gets smaller gives back to the advance of its order what it can no longer hold: the money stays reserved for the order, so it is not owed back.
    [Fact]
    public async Task A_smaller_invoice_gives_back_to_the_advance_and_the_order_completes_when_everything_is_delivered_but_never_reopens()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var lot = await SplitLotAsync(sale);
        var (orderId, lineId) = await OrderWithAdvanceAsync(sale, kg: 20m, advance: 500_000m);

        // 10 kg of 20 at 30,000 is 300,000, paid by the advance alone.
        var sold = (await owner.SellAsync(Invoice(sale.CustomerId, [KgLine(sale, lot, 10m, lineId)], Cash(0m), lot).With("salesOrderId", orderId))).GetProperty("invoice");
        Assert.Equal("Delivering", (await owner.GetOrderAsync(orderId)).Text("status"));
        Assert.Equal(200_000m, (await owner.GetOrderAsync(orderId)).Money("advanceLeft"));

        // Down to 4 kg (120,000): the newest part of the advance is cut, 180,000 return to the advance (380,000 are held for the order), and no debt is owed back.
        var smaller = (await owner.EditedAsync(sold.Id(), Edit(sold, [KgLine(sale, lot, 4m, lineId)], lot))).GetProperty("invoice");
        Assert.Equal(120_000m, smaller.Money("total"));
        var order = await owner.GetOrderAsync(orderId);
        Assert.Equal((380_000m, 4m), (order.Money("advanceLeft"), order.GetProperty("lines")[0].Money("deliveredQuantity")));
        Assert.Equal(120_000m, Assert.Single(await api.QueryAsync(db => db.CustomerPaymentAllocations.AsNoTracking().ToListAsync())).Amount);
        Assert.Equal("Delivering", order.Text("status"));

        // Up to everything ordered: the order completes by itself; lowering it again does not reopen it.
        var full = (await owner.EditedAsync(sold.Id(), Edit(smaller, [KgLine(sale, lot, 20m, lineId)], lot))).GetProperty("invoice");
        Assert.Equal("Completed", (await owner.GetOrderAsync(orderId)).Text("status"));
        // The order is Completed, so what is left of its advance is free credit: lowering the invoice leaves the customer owed 350,000, which is asked (500,000 paid, 150,000 left to pay).
        var lowering = Edit(full, [KgLine(sale, lot, 5m, lineId)], lot);
        Assert.Equal(350_000m, (await (await owner.EditAsync(sold.Id(), lowering)).QuestionAsync()).Body.Money("owedBackAmount"));
        await owner.EditedAsync(sold.Id(), lowering.With("confirmOwedBack", true));
        Assert.Equal("Completed", (await owner.GetOrderAsync(orderId)).Text("status"));
    }

    // The invoice of a Completed order is cancelled: the order is Delivering again, the advance is the order's again and held for it.
    [Fact]
    public async Task Cancelling_the_invoice_of_a_completed_order_reopens_it_and_holds_the_advance_for_it_again()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var (orderId, lineId) = await OrderWithAdvanceAsync(sale, kg: 10m, advance: 300_000m);
        var sold = (await owner.SellAsync(Invoice(sale.CustomerId, [KgLine(sale, sale.LotId, 10m, lineId)], Cash(0m), sale.LotId).With("salesOrderId", orderId))).GetProperty("invoice");
        var completed = await owner.GetOrderAsync(orderId);
        Assert.Equal(("Completed", 0m), (completed.Text("status"), completed.Money("advanceLeft")));
        Assert.Equal(0m, await owner.CustomerDebtAsync(sale.CustomerId));

        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.CancelInvoiceAsync(sold.Id(), Cancellation(sold))).ErrorCodeAsync());
        await owner.CancelledAsync(sold.Id(), Cancellation(sold));

        var reopened = await owner.GetOrderAsync(orderId);
        Assert.Equal(("Delivering", 300_000m, 0m), (reopened.Text("status"), reopened.Money("advanceLeft"), reopened.GetProperty("lines")[0].Money("deliveredQuantity")));
        Assert.Empty(await api.QueryAsync(db => db.CustomerPaymentAllocations.AsNoTracking().ToListAsync()));
        // The customer paid the advance: his debt is -300,000, all of it held for the order.
        Assert.Equal(-300_000m, await owner.CustomerDebtAsync(sale.CustomerId));
        var debt = await owner.GetFromJsonAsync<JsonElement>($"/api/customer-debts/{sale.CustomerId}");
        Assert.Equal(300_000m, Assert.Single(debt.GetProperty("reservedAdvances").EnumerateArray()).Money("amount"));
        Assert.Equal("Cancelled", (await owner.InvoiceAsync(sold.Id())).Text("status"));
    }
}
