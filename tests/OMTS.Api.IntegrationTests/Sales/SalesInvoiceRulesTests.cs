using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>BR-SALE-06 and BR-SALE-08: lots for sale in zones the user can access, products In Use, the zone of the invoice, who sells, the repeated save, and what HARVEST now reads of the sales.</summary>
public class SalesInvoiceRulesTests
{
    // SALE-05.0.E2: a lot closed after it was chosen is named.
    [Fact]
    public async Task A_lot_no_longer_for_sale_is_named_with_msg_gen_09_and_a_discontinued_product_too()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var client = sale.Manager.Client;
        var (orderId, _) = await client.OrderOfAsync(sale.CustomerId, (sale.ProductId, 5m));
        await api.UpdateAsync<HarvestLot>(sale.LotId, l => (l.StatusId, l.CloseDate, l.LeftoverKg) = (StatusIds.HarvestLot.Closed, FarmingTestKit.Today, 0m));
        var code = (await sale.Owner.Client.GetHarvestLotAsync(sale.LotId)).Text("lotCode");

        var refused = await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.OtherLotId, 1m), Line(sale.ProductId, sale.LotId, 1m)], Cash(60_000m))))
            .ReadAsync(HttpStatusCode.Conflict);
        Assert.Equal(("MSG-GEN-09", $"Harvest lot {code} is Closed and cannot be used for this action."), (refused.Text("code"), refused.Text("detail")));

        (await sale.Owner.Client.PostAsync($"/api/products/{sale.ProductId}/discontinue", null)).EnsureSuccessStatusCode();
        // The order was made while the product was in use; the invoice of it is refused once the product is discontinued.
        var discontinued = await (await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.OtherLotId, 1m)], Cash(30_000m)).With("salesOrderId", orderId))).ReadAsync(HttpStatusCode.BadRequest);
        Assert.Equal(("MSG-GEN-09", "Line 1 product"), (discontinued.Text("code"), discontinued.Text("field")));
        Assert.Equal(0, await api.QueryAsync(db => db.SalesInvoices.CountAsync()));
    }

    // §I.4.4: the Zone Manager in the assigned zones and the Owner; the Accountant views, a trader does nothing here.
    [Fact]
    public async Task Lots_and_the_zone_of_the_invoice_must_be_zones_the_seller_can_access_and_the_accountant_cannot_sell()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var otherZone = (await sale.Owner.Client.CreateZoneAsync()).Id();
        var outsider = await api.StaffAsync(await api.ZoneEntityAsync(otherZone));
        var accountant = await api.StaffAsync(null, StaffRole.Accountant);
        var (orderId, _) = await sale.Owner.Client.OrderOfAsync(sale.CustomerId, (sale.ProductId, 1m));
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m)).With("salesOrderId", orderId);

        // An order of another zone is not the seller's to invoice, whichever lot the invoice names.
        Assert.Equal("MSG-GEN-08", await (await outsider.Client.PostInvoiceAsync(body)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await accountant.Client.PostInvoiceAsync(body)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await (await api.TraderAsync()).Client.PostInvoiceAsync(body)).ErrorCodeAsync());
        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m)).With("zoneId", otherZone))).ErrorCodeAsync());
        Assert.Equal(["Zone"], await (await sale.Owner.Client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m)).With("zoneId", Guid.NewGuid()))).InvalidFieldsAsync());

        // The Owner may count the sale in another zone (BR-SALE-08); every revenue then counts there.
        var elsewhere = (await sale.Owner.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m), sale.LotId).With("zoneId", otherZone))).GetProperty("invoice");
        Assert.Equal(otherZone, elsewhere.GetProperty("zone").Id());

        // Viewing follows the zone of the invoice: the Accountant (All zones) yes, the Zone Manager of the lot's zone no.
        Assert.Equal(HttpStatusCode.OK, (await accountant.Client.GetAsync($"/api/sales-invoices/{elsewhere.Id()}")).StatusCode);
        Assert.Equal("MSG-GEN-08", await (await sale.Manager.Client.GetAsync($"/api/sales-invoices/{elsewhere.Id()}")).ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await accountant.Client.GetAsync($"/api/sales-invoices/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task A_merged_profile_cannot_buy_and_its_debt_counts_with_the_kept_one_after_a_merge()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var duplicate = (await sale.Owner.Client.CreateCustomerAsync()).Id();
        await sale.Manager.Client.SellAsync(Invoice(duplicate, [Line(sale.ProductId, sale.LotId, 10m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));
        Assert.Equal(300_000m, await sale.Owner.Client.CustomerDebtAsync(duplicate));

        var merged = await (await sale.Owner.Client.MergeAsync(sale.CustomerId, duplicate)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(300_000m, merged.Money("currentDebt"));
        Assert.Equal(sale.CustomerId, await api.QueryAsync(db => db.SalesInvoices.Select(i => i.CustomerId).SingleAsync()));
        Assert.Equal("MSG-GEN-09", await (await sale.Manager.Client.PostInvoiceAsync(Invoice(duplicate, [Line(sale.ProductId, sale.LotId, 1m)], Cash(30_000m)))).ErrorCodeAsync());
    }

    // A second tap on Save sells nothing twice.
    [Fact]
    public async Task The_same_deduplication_key_returns_the_first_invoice_and_creates_nothing()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var body = Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 10m)], Cash(300_000m)).With("deduplicationKey", "tap-1");

        var first = (await sale.Manager.Client.SellAsync(body)).GetProperty("invoice");
        var again = await sale.Manager.Client.SellAsync(body);

        Assert.Equal(first.Id(), again.GetProperty("invoice").Id());
        Assert.Equal("MSG-SALE-06", again.Text("messageCode"));
        Assert.Equal((1, 1), (await api.QueryAsync(db => db.SalesInvoices.CountAsync()), await api.QueryAsync(db => db.CustomerPayments.CountAsync())));
        Assert.Equal(1, await api.QueryAsync(db => db.Notifications.CountAsync(n => n.Type == "SALE_COMPLETED" && n.RecipientAccountId == sale.Owner.Account.Id)));
    }

    // BR-HARVEST-07 and BR-HARVEST-05 through the real sales port: invoiced kg count, a lot on an invoice is no longer removed, the remaining kg is for information only.
    [Fact]
    public async Task Harvest_reads_the_invoiced_kg_and_a_lot_on_an_invoice_can_no_longer_be_removed()
    {
        using var api = new AuthApiFactory();
        var sale = await api.SaleSetupAsync();
        var estimated = (await sale.Owner.Client.GetHarvestLotAsync(sale.OtherLotId)).Money("estimatedKg");
        var first = (await sale.Manager.Client.SellAsync(Invoice(null, [Line(sale.ProductId, sale.OtherLotId, 150.5m), Line(sale.ProductId, sale.OtherLotId, 49.5m)], Cash(6_000_000m), sale.OtherLotId))).GetProperty("invoice");

        var lot = await sale.Owner.Client.GetHarvestLotAsync(sale.OtherLotId);
        var figures = lot.GetProperty("figures");
        Assert.Equal((200m, estimated - 200m, 200m), (figures.Money("invoicedKg"), figures.Money("remainingKg"), figures.Money("soldKg")));
        Assert.False(lot.GetProperty("canRemove").GetBoolean());
        Assert.True(lot.GetProperty("canCorrect").GetBoolean());
        Assert.Equal("MSG-GEN-09", await (await api.SendRemovalAsync(sale.Owner.Client, sale.OtherLotId)).ErrorCodeAsync());
        var forSale = (await sale.Manager.Client.LotsForSaleAsync(sale.ZoneId)).GetProperty("lots").EnumerateArray().Single(l => l.Id() == sale.OtherLotId);
        Assert.Equal(estimated - 200m, forSale.Money("remainingKg"));

        // Sales are not limited by the remaining kg (BR-HARVEST-02): more than the estimate is sold, and the remaining kg stays at 0.
        var second = (await sale.Manager.Client.SellAsync(Invoice(null, [Line(sale.ProductId, sale.OtherLotId, estimated)], Cash(estimated * ListPrice), sale.OtherLotId))).GetProperty("invoice");
        Assert.Equal(0m, (await sale.Owner.Client.GetHarvestLotAsync(sale.OtherLotId)).GetProperty("figures").Money("remainingKg"));

        // A cancelled invoice counts nowhere, but keeps its lines (the removal guard still holds). SALE-08 comes with PR 10: set here.
        await api.UpdateAsync<SalesInvoice>(second.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);
        Assert.Equal(200m, (await sale.Owner.Client.GetHarvestLotAsync(sale.OtherLotId)).GetProperty("figures").Money("invoicedKg"));
        await api.UpdateAsync<SalesInvoice>(first.Id(), i => i.StatusId = StatusIds.SalesInvoice.Cancelled);
        Assert.Equal(0m, (await sale.Owner.Client.GetHarvestLotAsync(sale.OtherLotId)).GetProperty("figures").Money("invoicedKg"));
        Assert.False((await sale.Owner.Client.GetHarvestLotAsync(sale.OtherLotId)).GetProperty("canRemove").GetBoolean());
        Assert.True((await sale.Owner.Client.GetHarvestLotAsync(sale.LotId)).GetProperty("canRemove").GetBoolean());
    }
}
