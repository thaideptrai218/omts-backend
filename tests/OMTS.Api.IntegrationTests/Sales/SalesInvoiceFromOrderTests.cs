using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceChangeTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// An invoice made from an order (DH): the draft the order proposes, the lines that name their order line and unit, the price and the discount of the order, the advance that pays first, the progress of the order,
/// and the edit and the cancellation of such an invoice with the product ledger and the advance.
/// </summary>
public class SalesInvoiceFromOrderTests
{
    /// <summary>The order of the plan: 100 kg at 50,000 and 2 cages at 1,200,000 of one product (7,400,000), a discount of 100,000 and an advance of 500,000 in cash, taken by the Owner.</summary>
    private sealed record Plan(SaleSetup Sale, JsonElement Product, Guid LotId, Guid OrderId, Guid KgLineId, Guid CageLineId, Guid KgUnitId, Guid CageUnitId);

    private static async Task<Plan> PlanAsync(AuthApiFactory api, bool advance = true)
    {
        var sale = await api.SaleSetupAsync();
        var owner = sale.Owner.Client;
        var product = await (await owner.PostAsJsonAsync("/api/products", new
        {
            name = $"Hàu {Guid.NewGuid():N}"[..20], unitPrice = 50_000, units = new object[] { new { unitName = "Lồng", factor = 20, salePrice = 1_200_000 } },
        })).ReadAsync(HttpStatusCode.Created);
        var p = product.GetProperty("product");
        var units = p.GetProperty("units").EnumerateArray().ToDictionary(u => u.GetProperty("isBase").GetBoolean(), u => u.GetProperty("unitId").GetGuid());
        var lot = await owner.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((p.Id(), 8, 400m)));
        var body = new Dictionary<string, object?>
        {
            ["customerId"] = sale.CustomerId, ["walkInCustomer"] = false, ["source"] = "Phone", ["discount"] = 100_000, ["discountDecidedByOwner"] = true,
            ["lines"] = new object[]
            {
                new { productId = p.Id(), unitId = units[true], quantity = 100m },
                new { productId = p.Id(), unitId = units[false], quantity = 2m },
            },
        };
        if (advance)
        {
            body["advance"] = new { amount = 500_000, method = "Cash" };
        }

        var order = (await owner.CreateOrderAsync(body)).GetProperty("order");
        var lines = order.GetProperty("lines").EnumerateArray().Select(l => l.Id()).ToList();
        return new Plan(sale, p, lot.Id(), order.Id(), lines[0], lines[1], units[true], units[false]);
    }

    private static Dictionary<string, object?> KgLine(Plan plan, decimal kg, Guid? lotId = null, decimal? unitPrice = null) => new()
    {
        ["productId"] = plan.Product.Id(), ["harvestLotId"] = lotId ?? plan.LotId, ["kg"] = kg, ["salesOrderLineId"] = plan.KgLineId, ["unitPrice"] = unitPrice,
    };

    private static Dictionary<string, object?> CageLine(Plan plan, decimal cages, decimal kg) => new()
    {
        ["productId"] = plan.Product.Id(), ["harvestLotId"] = plan.LotId, ["kg"] = kg, ["quantity"] = cages, ["unitId"] = plan.CageUnitId, ["salesOrderLineId"] = plan.CageLineId,
    };

    private static Dictionary<string, object?> FromOrder(Plan plan, object[] lines, object payment) => new()
    {
        ["salesOrderId"] = plan.OrderId, ["lines"] = lines, ["payment"] = payment, ["notSoldWholeLotIds"] = new[] { plan.LotId },
    };

    private static Task<List<(ProductStockMovementKind Kind, decimal Kg)>> StockRowsAsync(AuthApiFactory api, Guid productId) =>
        api.QueryAsync(async db => (await db.ProductStockMovements.AsNoTracking().Where(m => m.ProductId == productId).OrderBy(m => m.RecordedAt).ToListAsync())
            .Select(m => (m.Kind, m.QuantityKg)).ToList());

    // The plan's smoke test: draft, invoice of 60 kg with its share of the discount and the advance, edit to 70 kg, cancel, then the order is cancelled.
    [Fact]
    public async Task An_invoice_of_an_order_takes_its_share_of_the_discount_and_the_advance_and_can_be_edited_and_cancelled()
    {
        using var api = new AuthApiFactory();
        var plan = await PlanAsync(api);
        var owner = plan.Sale.Owner.Client;
        var manager = plan.Sale.Manager.Client;

        var draft = await manager.GetFromJsonAsync<JsonElement>($"/api/sales-orders/{plan.OrderId}/invoice-draft");
        Assert.Equal((7_400_000m, 100_000m, 100_000m, 500_000m), (draft.Money("subtotal"), draft.Money("orderDiscount"), draft.Money("discountShare"), draft.Money("advanceLeft")));
        var draftLines = draft.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([100m, 2m], draftLines.Select(l => l.Money("quantity")));
        Assert.Equal([50_000m, 1_200_000m], draftLines.Select(l => l.Money("unitPrice")));
        Assert.Equal(plan.LotId, draftLines[0].GetProperty("suggestedLotId").GetGuid());
        // The order has no zone: the draft proposes the zone of the first lot it proposes, and the invoice takes the zone of its first lot whatever zone the request names.
        Assert.Equal(plan.Sale.ZoneId, draft.GetProperty("zone").Id());

        // 60 kg of the 100: 100,000 × 3,000,000 / 7,400,000 = 40,541 of the discount, the advance of 500,000 pays first, the cash settles the rest.
        var saved = await manager.SellAsync(FromOrder(plan, [KgLine(plan, 60m)], Cash(2_459_459m)).With("discount", 40_541m).With("zoneId", Guid.NewGuid()));
        var invoice = saved.GetProperty("invoice");
        Assert.Equal(plan.Sale.ZoneId, invoice.GetProperty("zone").Id());
        Assert.Equal((2_959_459m, "OrderPrice"), (invoice.Money("total"), invoice.GetProperty("lines")[0].Text("priceSource")));
        Assert.Equal(("Delivering", true), (saved.GetProperty("order").Text("status"), saved.GetProperty("orderCanFinish").GetBoolean()));
        Assert.Equal((plan.OrderId, "kg"), (invoice.GetProperty("salesOrder").Id(), invoice.GetProperty("lines")[0].GetProperty("unit").Text("name")));
        var allocation = Assert.Single(await api.QueryAsync(db => db.CustomerPaymentAllocations.AsNoTracking().Where(a => a.InvoiceId == invoice.Id()).ToListAsync()));
        Assert.Equal(500_000m, allocation.Amount);
        Assert.True(invoice.GetProperty("debtEffect").GetProperty("isPaid").GetBoolean());
        Assert.Equal(0m, await owner.CustomerDebtAsync(plan.Sale.CustomerId));

        var order = await owner.GetOrderAsync(plan.OrderId);
        Assert.Equal((60m, 2_959_459m, 0m), (order.GetProperty("lines")[0].Money("deliveredQuantity"), order.Money("invoicedTotal"), order.Money("advanceLeft")));
        Assert.Equal(invoice.Text("code"), Assert.Single(order.GetProperty("invoices").EnumerateArray()).Text("code"));
        Assert.Equal(("kg", 60m, 100m), (order.GetProperty("progress")[0].Text("unitName"), order.GetProperty("progress")[0].Money("delivered"), order.GetProperty("progress")[0].Money("ordered")));
        Assert.DoesNotContain("Cancel", order.AllowedActions());

        // The ledger: the harvest and the sale of 60 kg.
        Assert.Equal([(ProductStockMovementKind.Harvest, 400m), (ProductStockMovementKind.Sale, -60m)], await StockRowsAsync(api, plan.Product.Id()));

        // The edit (the discount is sent again: an edit does not work the share out again): 70 kg, the debt grows by 500,000, the ledger reverses the old row and posts the new one.
        var current = await owner.InvoiceAsync(invoice.Id());
        var edited = (await owner.EditedAsync(invoice.Id(), Edit(current, [KgLine(plan, 70m)]).With("discount", 40_541m))).GetProperty("invoice");
        Assert.Equal(3_459_459m, edited.Money("total"));
        Assert.Equal(500_000m, await owner.CustomerDebtAsync(plan.Sale.CustomerId));
        Assert.Equal(
            [(ProductStockMovementKind.Harvest, 400m), (ProductStockMovementKind.Sale, -60m), (ProductStockMovementKind.Reversal, 60m), (ProductStockMovementKind.Sale, -70m)],
            await StockRowsAsync(api, plan.Product.Id()));
        Assert.Equal(70m, (await owner.GetOrderAsync(plan.OrderId)).GetProperty("lines")[0].Money("deliveredQuantity"));

        // The cancellation takes the part of the advance off, gives the cash back, and reverses the sale; the order is still Delivering and cannot be cancelled before.
        var cancelOrderFirst = await owner.PostAsJsonAsync($"{Route}/{plan.OrderId}/cancel", new { reason = "Customer changed his mind" });
        Assert.Equal("MSG-ORDER-01", (await cancelOrderFirst.ReadAsync(HttpStatusCode.Conflict)).Text("code"));
        var payment = Assert.Single(await api.PaymentsOfAsync(invoice.Id()));
        await owner.CancelledAsync(invoice.Id(), Cancellation(edited).With("cashGivenBack", GivenBack(payment.Id, true)));

        var afterCancel = await owner.GetOrderAsync(plan.OrderId);
        Assert.Equal(("Delivering", 0m, 500_000m), (afterCancel.Text("status"), afterCancel.GetProperty("lines")[0].Money("deliveredQuantity"), afterCancel.Money("advanceLeft")));
        Assert.Empty(await api.QueryAsync(db => db.CustomerPaymentAllocations.AsNoTracking().Where(a => a.InvoiceId == invoice.Id()).ToListAsync()));
        var sold = (await StockRowsAsync(api, plan.Product.Id())).Where(r => r.Kind != ProductStockMovementKind.Harvest).Sum(r => r.Kg);
        Assert.Equal(0m, sold);
        // The advance payment is never cancelled with the invoice: it is still the cash taken on the order, awaiting its confirmation.
        Assert.Equal(StatusIds.CustomerPayment.AwaitingConfirmation, Assert.Single(await api.AdvancesOfOrderAsync(plan.OrderId)).StatusId);
        Assert.Equal("Cancelled", (await (await owner.PostAsJsonAsync($"{Route}/{plan.OrderId}/cancel", new { reason = "Customer changed his mind" })).ReadAsync(HttpStatusCode.OK)).GetProperty("order").Text("status"));
    }

    [Fact]
    public async Task A_line_in_another_unit_is_priced_by_that_unit_and_the_order_completes_when_every_line_is_delivered()
    {
        using var api = new AuthApiFactory();
        var plan = await PlanAsync(api, advance: false);
        var manager = plan.Sale.Manager.Client;

        // 2 cages at 1,200,000 weighing 38.5 kg, and 100 kg at 50,000: the money follows the unit of the line, the ledger the kg weighed.
        var saved = await manager.SellAsync(FromOrder(plan, [KgLine(plan, 100m), CageLine(plan, 2m, 38.5m)], Cash(7_300_000m)).With("discount", 100_000m));

        var lines = saved.GetProperty("invoice").GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal((5_000_000m, 2_400_000m, 2m, 38.5m), (lines[0].Money("amount"), lines[1].Money("amount"), lines[1].Money("quantity"), lines[1].Money("kg")));
        Assert.Equal(("Lồng", 1_200_000m), (lines[1].GetProperty("unit").Text("name"), lines[1].Money("unitPrice")));
        Assert.Equal(("Completed", false), (saved.GetProperty("order").Text("status"), saved.GetProperty("orderCanFinish").GetBoolean()));
        Assert.Equal(-38.5m, (await StockRowsAsync(api, plan.Product.Id()))[^1].Kg);
        // The kg ledger prices the cages by the kg: 2,400,000 / 38.5 = 62,337.66 a kg.
        var price = await api.QueryAsync(db => db.ProductStockMovements.AsNoTracking().Where(m => m.ProductId == plan.Product.Id() && m.Kind == ProductStockMovementKind.Sale && m.QuantityKg == -38.5m)
            .Select(m => m.TransactionPrice).SingleAsync());
        Assert.Equal(62_338m, price);
    }

    [Fact]
    public async Task An_order_that_is_not_open_or_a_customer_that_is_not_its_own_is_refused()
    {
        using var api = new AuthApiFactory();
        var plan = await PlanAsync(api, advance: false);
        var manager = plan.Sale.Manager.Client;
        var owner = plan.Sale.Owner.Client;
        var other = await owner.CreateCustomerAsync();

        var withoutOrder = await manager.PostAsJsonAsync("/api/sales-invoices", new { lines = new[] { KgLine(plan, 1m) }, payment = Cash(50_000m), customerId = plan.Sale.CustomerId, walkInCustomer = false });
        Assert.Equal(["Sales order"], await withoutOrder.InvalidFieldsAsync());

        var wrongCustomer = await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 1m)], Cash(50_000m)).With("customerId", other.Id()).With("walkInCustomer", false));
        var refused = await wrongCustomer.ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-ORDER-06", "Customer"), (refused.Text("code"), refused.Text("field")));
        var requestLink = await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 1m)], Cash(50_000m)).With("purchaseRequestId", Guid.NewGuid()));
        Assert.Equal(["Purchase request"], await requestLink.InvalidFieldsAsync());
        var strangeLine = KgLine(plan, 1m);
        strangeLine["salesOrderLineId"] = Guid.NewGuid();
        Assert.Equal(["Line 1 order line"], await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [strangeLine], Cash(50_000m)))).InvalidFieldsAsync());

        (await owner.PostAsJsonAsync($"{Route}/{plan.OrderId}/cancel", new { reason = "Customer changed his mind" })).EnsureSuccessStatusCode();
        var closed = await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 1m)], Cash(50_000m)))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-ORDER-05", closed.Text("code"));
        var draft = await (await manager.GetAsync($"{Route}/{plan.OrderId}/invoice-draft")).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal("MSG-ORDER-05", draft.Text("code"));
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    [Fact]
    public async Task The_price_of_the_order_line_is_asked_about_and_a_discount_within_the_share_is_not()
    {
        using var api = new AuthApiFactory();
        var plan = await PlanAsync(api, advance: false);
        var manager = plan.Sale.Manager.Client;

        var price = await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 10m, unitPrice: 55_000m)], Cash(550_000m)))).QuestionAsync();
        Assert.Equal(("MSG-SALE-10", "lines[0].priceDecidedByOwner"), (price.Code, price.Field));

        // 10 kg is 500,000 of 7,400,000: its share of the 100,000 discount is 6,757; a discount of 6,758 is above it and is asked.
        var within = await manager.SellAsync(FromOrder(plan, [KgLine(plan, 10m)], Cash(493_243m)).With("discount", 6_757m));
        Assert.Equal(493_243m, within.GetProperty("invoice").Money("total"));
        var above = await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 10m)], Cash(493_000m)).With("discount", 6_758m))).QuestionAsync();
        Assert.Equal(("MSG-SALE-10", "discountDecidedByOwner"), (above.Code, above.Field));
    }

    [Fact]
    public async Task The_advance_of_an_order_pays_no_invoice_of_another_order_and_leftover_is_credit_once_the_order_ends()
    {
        using var api = new AuthApiFactory();
        var plan = await PlanAsync(api);
        var owner = plan.Sale.Owner.Client;
        var manager = plan.Sale.Manager.Client;

        // An invoice of another order of the same customer, on the debt: the advance reserved for the first order does not pay it.
        var (otherOrder, otherLines) = await owner.OrderOfAsync(plan.Sale.CustomerId, (plan.Sale.ProductId, 10m));
        var sold = (await owner.SellAsync(new Dictionary<string, object?>
        {
            ["salesOrderId"] = otherOrder, ["lines"] = new object[] { new Dictionary<string, object?> { ["productId"] = plan.Sale.ProductId, ["harvestLotId"] = plan.Sale.LotId, ["kg"] = 10m, ["salesOrderLineId"] = otherLines[0] } },
            ["payment"] = Cash(0m, "PayLater", payLaterOwnerAgreed: true),
        })).GetProperty("invoice");

        Assert.Equal((300_000m, false), (sold.GetProperty("debtEffect").Money("unpaid"), sold.GetProperty("debtEffect").GetProperty("isPaid").GetBoolean()));
        var debt = await owner.GetFromJsonAsync<JsonElement>($"/api/customer-debts/{plan.Sale.CustomerId}");
        Assert.Equal(500_000m, Assert.Single(debt.GetProperty("reservedAdvances").EnumerateArray()).Money("amount"));

        // 4 kg of the first order (200,000) are paid by its advance alone; the 300,000 left stay reserved, and the invoice of the other order is still unpaid.
        await manager.SellAsync(FromOrder(plan, [KgLine(plan, 4m)], Cash(0m)));
        var reserved = await owner.GetFromJsonAsync<JsonElement>($"/api/customer-debts/{plan.Sale.CustomerId}");
        Assert.Equal(300_000m, Assert.Single(reserved.GetProperty("reservedAdvances").EnumerateArray()).Money("amount"));
        Assert.Equal(300_000m, (await owner.InvoiceAsync(sold.Id())).GetProperty("debtEffect").Money("unpaid"));

        // Finishing the first order frees the leftover: it is general credit and pays the oldest unpaid invoice.
        (await owner.PostAsJsonAsync($"{Route}/{plan.OrderId}/finish", new { })).EnsureSuccessStatusCode();
        var after = await owner.GetFromJsonAsync<JsonElement>($"/api/customer-debts/{plan.Sale.CustomerId}");
        Assert.Empty(after.GetProperty("reservedAdvances").EnumerateArray());
        Assert.True((await owner.InvoiceAsync(sold.Id())).GetProperty("debtEffect").GetProperty("isPaid").GetBoolean());
    }

    [Fact]
    public async Task A_typed_code_is_kept_and_a_taken_one_is_refused_and_the_next_code_is_offered()
    {
        using var api = new AuthApiFactory();
        var plan = await PlanAsync(api, advance: false);
        var manager = plan.Sale.Manager.Client;

        var next = await manager.GetFromJsonAsync<JsonElement>("/api/sales-invoices/next-code");
        Assert.Matches("^HD[0-9]{6}$", next.Text("code")!);
        var first = (await manager.SellAsync(FromOrder(plan, [KgLine(plan, 1m)], Cash(50_000m)).With("code", "hd-A.1").With("note", "Weighed twice"))).GetProperty("invoice");
        Assert.Equal(("hd-A.1", "Weighed twice"), (first.Text("code"), first.Text("note")));
        var taken = await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 1m)], Cash(50_000m)).With("code", "HD-a.1"))).ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-02", "Code", first.Id()), (taken.Text("code"), taken.Text("field"), taken.GetProperty("existingId").GetGuid()));
        Assert.Equal(["Code"], await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 1m)], Cash(50_000m)).With("code", "bad code!"))).InvalidFieldsAsync());
        Assert.Equal(["Code"], await (await manager.PostAsJsonAsync("/api/sales-invoices", FromOrder(plan, [KgLine(plan, 1m)], new { cashAmount = 0, rest = "VietQr" }).With("code", new string('A', 26)))).InvalidFieldsAsync());
        var deprecated = await manager.GetFromJsonAsync<JsonElement>($"/api/sales-invoices/open-purchase-requests?customerId={plan.Sale.CustomerId}");
        Assert.Empty(deprecated.EnumerateArray());
    }
}
