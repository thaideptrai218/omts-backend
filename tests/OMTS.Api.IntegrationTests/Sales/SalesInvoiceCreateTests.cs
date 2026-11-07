using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-05 and AC-06: lines at the list price or the price the Owner decided, the total, the cash payment, the lot closes, the audit and the Owner's notice.</summary>
public class SalesInvoiceCreateTests
{
    // AC-06: 200 kg of milk oysters at 30,000 VND/kg is a line of 6,000,000 VND, at the list price.
    [Fact]
    public async Task Ac_06_a_line_of_200_kg_at_the_list_price_is_6_000_000_and_the_sale_is_saved_with_its_cash_payment_and_lot_close()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();

        var saved = await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 200m)], Cash(6_000_000m)));

        var invoice = saved.GetProperty("invoice");
        Assert.Matches("^HD[0-9]{6}$", invoice.Text("code"));
        var line = Assert.Single(invoice.GetProperty("lines").EnumerateArray());
        Assert.Equal((200m, 30_000m, 30_000m, "PriceList", 6_000_000m, true),
            (line.Money("kg"), line.Money("unitPrice"), line.Money("listPrice"), line.Text("priceSource"), line.Money("amount"), line.GetProperty("soldWholeLot").GetBoolean()));
        Assert.Equal((6_000_000m, 6_000_000m, 0m, 0m, "PayNow", "Completed"),
            (invoice.Money("subtotal"), invoice.Money("total"), invoice.Money("discount"), invoice.Money("otherCharges"), invoice.Text("paymentTerm"), invoice.Text("status")));
        Assert.Equal(sale.ZoneId, invoice.GetProperty("zone").Id());
        Assert.Equal(sale.CustomerId, invoice.GetProperty("customer").Id());
        Assert.Equal(sale.Manager.Account.Id, invoice.GetProperty("createdBy").Id());
        Assert.Equal(("MSG-SALE-06", $"Invoice {invoice.Text("code")} is completed: 6,000,000 VND collected in cash."), (saved.Text("messageCode"), saved.Text("message")));
        Assert.Equal(0m, saved.Money("change"));

        // POST-2: one cash payment of the invoice, Awaiting confirmation, with the Zone Manager who collected it.
        var payment = Assert.Single(invoice.GetProperty("payments").EnumerateArray());
        Assert.Matches("^PT[0-9]{6}$", payment.Text("code"));
        Assert.Equal(("Cash", "Payment", 6_000_000m, "Awaiting confirmation", sale.Manager.Account.Id),
            (payment.Text("method"), payment.Text("kind"), payment.Money("amount"), payment.Text("status"), payment.GetProperty("collectedBy").Id()));

        // POST-3: the ticked lot is Closed, Sold whole lot, leftover 0, naming this invoice; the other lot stays for sale.
        var lot = await api.QueryAsync(db => db.HarvestLots.AsNoTracking().SingleAsync(l => l.Id == sale.LotId));
        Assert.Equal((StatusIds.HarvestLot.Closed, HarvestLotCloseReason.SoldWholeLot, 0m, invoice.Id()), (lot.StatusId, lot.CloseReason, lot.LeftoverKg, lot.ClosedBySalesInvoiceId));
        Assert.Equal(StatusIds.HarvestLot.Open, await api.QueryAsync(db => db.HarvestLots.Where(l => l.Id == sale.OtherLotId).Select(l => l.StatusId).SingleAsync()));

        // POST-6 and GBR-02: the invoice, its payment and the lot close in the audit log.
        var actions = await api.QueryAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.RecordId == invoice.Id() || e.RecordId == payment.Id() || e.RecordId == sale.LotId).Select(e => e.Action).ToListAsync());
        Assert.Contains("SalesInvoice.Created", actions);
        Assert.Contains("CustomerPayment.Recorded", actions);
        Assert.Contains("HarvestLot.Closed", actions);

        // The answer reads back the same through GET.
        var read = await (await sale.Manager.Client.GetAsync($"/api/sales-invoices/{invoice.Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(invoice.GetRawText(), read.GetRawText());
    }

    [Fact]
    public async Task A_line_of_0_kg_and_other_shapes_are_refused_with_msg_gen_01_and_nothing_is_saved()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;

        Assert.Equal(["Line 1 kg"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 0m)], Cash(0m)))).InvalidFieldsAsync());
        Assert.Equal(["Line 1 kg"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1.0001m)], Cash(0m)))).InvalidFieldsAsync());
        Assert.Equal(["Lines"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [], Cash(0m)))).InvalidFieldsAsync());
        Assert.Equal(["Line 1 unit price"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m, unitPrice: 0m)], Cash(0m)))).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m)).With("walkInCustomer", true))).InvalidFieldsAsync());
        Assert.Equal(["Customer"], await (await client.PostInvoiceAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m)).With("walkInCustomer", false))).InvalidFieldsAsync());
        Assert.Equal(["Sold the whole lot"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m), sale.OtherLotId))).InvalidFieldsAsync());
        Assert.Equal(["Discount"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(0m)).With("discount", 0.5m))).InvalidFieldsAsync());
        Assert.Equal(["Payment"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(0.5m)))).InvalidFieldsAsync());
        // BR-SALE-06: the discount cannot bring the total below 0 (the Owner is asked nothing, so the total is what is refused).
        Assert.Equal(["Discount"], await (await sale.Owner.Client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(0m)).With("discount", 30_001m))).InvalidFieldsAsync());
        // A lot or a product that does not exist.
        Assert.Equal(["Line 1 harvest lot"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, Guid.NewGuid(), 1m)], Cash(30_000m)))).InvalidFieldsAsync());
        Assert.Equal(["Line 1 product"], await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(Guid.NewGuid(), sale.LotId, 1m)], Cash(30_000m)))).InvalidFieldsAsync());

        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.CustomerPayments.CountAsync()));
        Assert.Equal(StatusIds.HarvestLot.Open, await api.QueryAsync(db => db.HarvestLots.Where(l => l.Id == sale.LotId).Select(l => l.StatusId).SingleAsync()));
    }

    // AC-06 and SALE-05.5: a different price or a discount is kept only after MSG-SALE-10; the Owner is not asked.
    [Fact]
    public async Task A_price_other_than_the_list_price_and_a_discount_need_the_owner_decision_and_keep_the_list_price_on_the_line()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var product = (await sale.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/products/{sale.ProductId}")).Text("name");

        var (code, field, body) = await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 100m, unitPrice: 28_000m)], Cash(2_800_000m)))).QuestionAsync();
        Assert.Equal(("MSG-SALE-10", "lines[0].priceDecidedByOwner"), (code, field));
        Assert.Equal($"{product}: the list price is 30,000 VND/kg, and you entered 28,000 VND/kg. Has the Cooperative Owner decided this price?", body.Text("detail"));

        var discounted = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 100m, unitPrice: 28_000m, priceDecidedByOwner: true)], Cash(2_600_000m)).With("discount", 200_000m);
        var (discountCode, discountField, discountBody) = await (await client.PostInvoiceAsync(discounted)).QuestionAsync();
        Assert.Equal(("MSG-SALE-10", "discountDecidedByOwner"), (discountCode, discountField));
        Assert.Contains("Has the Cooperative Owner decided a discount of 200,000 VND on this invoice?", discountBody.GetRawText());

        var saved = (await client.SellAsync(discounted.With("discountDecidedByOwner", true))).GetProperty("invoice");
        var line = saved.GetProperty("lines")[0];
        Assert.Equal(("DecidedByOwner", 28_000m, 30_000m, 2_800_000m), (line.Text("priceSource"), line.Money("unitPrice"), line.Money("listPrice"), line.Money("amount")));
        Assert.Equal((200_000m, true, 2_600_000m), (saved.Money("discount"), saved.GetProperty("discountDecidedByOwner").GetBoolean(), saved.Money("total")));

        // MSG-SALE-08 names the decided price and the discount.
        var notice = await api.QueryAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.Type == "SALE_COMPLETED" && n.RelatedRecordId == saved.Id()));
        Assert.EndsWith(" Price decided by the Cooperative Owner: 28,000 VND/kg (list price 30,000); discount 200,000 VND.", notice.Body);

        // The Owner sells at another price with no question; the source is still the Owner's decision.
        var byOwner = (await sale.Owner.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.OtherLotId, 10m, unitPrice: 25_000m)], Cash(250_000m)))).GetProperty("invoice");
        Assert.Equal("DecidedByOwner", byOwner.GetProperty("lines")[0].Text("priceSource"));
    }

    // BR-SALE-05: a new list price applies to the invoices created after it; an invoice keeps its unit prices.
    [Fact]
    public async Task A_new_list_price_changes_the_next_invoices_only()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var first = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m), sale.LotId))).GetProperty("invoice");
        var product = await sale.Owner.Client.GetFromJsonAsync<JsonElement>($"/api/products/{sale.ProductId}");
        (await sale.Owner.Client.PutAsJsonAsync($"/api/products/{sale.ProductId}", new { name = product.Text("name"), unitPrice = 35_000m, rowVersion = product.RowVersion() })).EnsureSuccessStatusCode();

        var second = (await sale.Manager.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(350_000m)))).GetProperty("invoice");

        Assert.Equal(30_000m, (await sale.Manager.Client.GetFromJsonAsync<JsonElement>($"/api/sales-invoices/{first.Id()}")).GetProperty("lines")[0].Money("unitPrice"));
        Assert.Equal(("PriceList", 35_000m), (second.GetProperty("lines")[0].Text("priceSource"), second.GetProperty("lines")[0].Money("unitPrice")));
    }

    // BR-SALE-03: the Owner is notified of each sale, the Owner's own included; the notice opens the invoice.
    [Fact]
    public async Task The_owner_is_told_of_each_sale_with_msg_sale_08_also_of_their_own()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();

        var byManager = (await sale.Manager.Client.SellAsync(Invoice(null, [Line(sale.ProductId, sale.LotId, 12.5m)], Cash(375_000m), sale.LotId))).GetProperty("invoice");
        var byOwner = (await sale.Owner.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 2m)], Cash(60_000m)))).GetProperty("invoice");

        var notices = await api.QueryAsync(db => db.Notifications.AsNoTracking().Where(n => n.Type == "SALE_COMPLETED" && n.RecipientAccountId == sale.Owner.Account.Id).ToListAsync());
        var managerName = sale.Manager.Account.DisplayName;
        var walkIn = notices.Single(n => n.RelatedRecordId == byManager.Id());
        Assert.Equal(($"{managerName} sold 12.5 kg to a walk-in customer for 375,000 VND (invoice {byManager.Text("code")}, Pay now).", "MSG-SALE-08", "SalesInvoice", byManager.Text("code")),
            (walkIn.Body, walkIn.MessageCode, walkIn.RelatedRecordType, walkIn.RelatedRecordCode));
        Assert.Contains(notices, n => n.RelatedRecordId == byOwner.Id());
        Assert.False(walkIn.IsMandatory);

        // A walk-in invoice has no customer, and its cash payment none either (BR-SALE-07 (8)).
        Assert.True(byManager.GetProperty("walkInCustomer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, byManager.GetProperty("customer").ValueKind);
        Assert.Null(Assert.Single(await api.PaymentsOfAsync(byManager.Id())).CustomerId);
    }
}
