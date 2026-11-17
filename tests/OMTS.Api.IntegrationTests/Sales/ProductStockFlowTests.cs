using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The stock of the products: a harvest lot split per product raises it, an edited or removed lot and an invoice change it through the ledger, a lot recorded the old way counts nowhere, and the Xuất hủy hàu
/// disposals (XHS) take kg off a product on a lot of their zone, completed, edited, cancelled and removed.
/// </summary>
public class ProductStockFlowTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static async Task<decimal> OnHandAsync(HttpClient client, Guid productId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}")).GetProperty("onHandKg").GetDecimal();

    private static async Task<JsonElement> MovementsAsync(HttpClient client, Guid productId, string query = "") =>
        await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}/movements{query}");

    private static async Task<JsonElement> NewProductWithCageAsync(HttpClient client, decimal cageKg = 25m)
    {
        var body = await (await client.PostAsJsonAsync("/api/products", new
        {
            name = $"Hàu {NewCode()}", unitPrice = 60_000, units = new object[] { new { unitName = "Lồng", factor = cageKg } },
        })).ReadAsync(HttpStatusCode.Created);
        return body.GetProperty("product");
    }

    [Fact]
    public async Task A_harvest_split_per_product_raises_each_stock_and_the_lot_cages_and_kg_are_the_sums_of_its_lines()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var cage = await NewProductWithCageAsync(client);

        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m), (cage.Id(), 3, null)));

        Assert.Equal((7, 175m), (lot.GetProperty("cageCount").GetInt32(), lot.Kg("estimatedKg")));
        var lines = lot.GetProperty("productLines").EnumerateArray().ToList();
        Assert.Equal([100m, 75m], lines.Select(l => l.Kg("kg")));
        Assert.Equal(cage.Text("code"), lines[1].Text("productCode"));
        Assert.Equal((100m, 75m), (await OnHandAsync(client, sale.ProductId), await OnHandAsync(client, cage.Id())));
        var movement = (await MovementsAsync(client, sale.ProductId)).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("Harvest", "HarvestLot", lot.Text("lotCode"), lot.Text("lotCode"), 100m), (movement.Text("kind"), movement.Text("documentKind"), movement.Text("documentCode"), movement.Text("lotCode"), movement.Kg("quantityKg")));
    }

    [Fact]
    public async Task A_lot_recorded_without_product_lines_counts_in_no_stock()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;

        await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3));

        Assert.Equal(0m, await OnHandAsync(client, sale.ProductId));
        Assert.Empty((await MovementsAsync(client, sale.ProductId)).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(2, 0)]
    [InlineData(2, -3)]
    public async Task A_line_needs_cages_and_kg_above_zero(int cages, double kg)
    {
        var sale = await factory.SaleSetupAsync();

        var response = await sale.Owner.Client.PostHarvestAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, cages, (decimal)kg)));

        await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");
    }

    [Fact]
    public async Task A_correction_undoes_the_rows_of_the_lot_and_writes_the_new_ones_and_a_removal_gives_the_kg_back()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));

        await factory.CorrectLotAsync(client, lot.Id(), HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 3, 60m)));

        Assert.Equal(60m, await OnHandAsync(client, sale.ProductId));
        Assert.Equal(["Harvest"], (await MovementsAsync(client, sale.ProductId)).GetProperty("items").EnumerateArray().Select(i => i.Text("kind")));
        var withReversals = (await MovementsAsync(client, sale.ProductId, "?includeReversals=true")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, withReversals.Count);
        Assert.Contains(withReversals, i => i.Text("kind") == "Reversal" && i.Kg("quantityKg") == -100m);

        await factory.RemoveLotAsync(client, lot.Id());

        Assert.Equal(0m, await OnHandAsync(client, sale.ProductId));
    }

    [Fact]
    public async Task An_invoice_takes_the_kg_of_its_lines_off_the_product_and_the_lot_shows_what_was_harvested_sold_and_left()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));

        var invoice = (await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, lot.Id(), 30m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lot.Id()))).GetProperty("invoice");

        Assert.Equal(70m, await OnHandAsync(client, sale.ProductId));
        var sold = (await MovementsAsync(client, sale.ProductId, "?kind=Sale")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal((invoice.Text("code"), "SalesInvoice", -30m, ListPrice), (sold.Text("documentCode"), sold.Text("documentKind"), sold.Kg("quantityKg"), sold.Kg("transactionPrice")));
        Assert.False(string.IsNullOrEmpty(sold.Text("partyCode")));
        var lots = await client.GetFromJsonAsync<JsonElement>($"/api/products/{sale.ProductId}/harvest-lots");
        var row = lots.GetProperty("lots").EnumerateArray().Single(l => l.Text("lotCode") == lot.Text("lotCode"));
        Assert.Equal((100m, 30m, 0m, 70m), (row.Kg("harvestedKg"), row.Kg("soldKg"), row.Kg("disposedKg"), row.Kg("remainingKg")));
    }

    [Fact]
    public async Task A_sale_from_a_lot_recorded_before_products_writes_no_row()
    {
        var sale = await factory.SaleSetupAsync();

        await sale.Owner.Client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 30m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));

        Assert.Empty((await MovementsAsync(sale.Owner.Client, sale.ProductId)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task The_lots_for_sale_list_the_kg_left_of_each_product()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, lot.Id(), 30m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lot.Id()));
        // The lot was ticked "not sold whole", so it is still for sale.

        var forSale = (await client.GetFromJsonAsync<JsonElement>("/api/harvest-lots/for-sale")).GetProperty("lots").EnumerateArray().Single(l => l.Id() == lot.Id());

        var product = forSale.GetProperty("products").EnumerateArray().Single();
        Assert.Equal((sale.ProductId, 100m, 70m), (product.GetProperty("productId").GetGuid(), product.Kg("harvestedKg"), product.Kg("remainingKg")));
    }

    // ---- Xuất hủy hàu ----

    private static object Disposal(Guid zoneId, Guid productId, Guid lotId, decimal kg, bool complete, string? reason = "Hàu chết do mưa lớn") => new
    {
        zoneId, reason, complete, lines = new[] { new { productId, harvestLotId = lotId, kg } },
    };

    [Fact]
    public async Task A_disposal_draft_holds_no_kg_completing_it_takes_them_off_and_cancelling_gives_them_back()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));

        var draft = await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 20m, complete: false))).ReadAsync(HttpStatusCode.Created);
        Assert.Matches("^XHS[0-9]{6,}$", draft.Text("code"));
        Assert.Equal(100m, await OnHandAsync(client, sale.ProductId));

        var completed = await (await client.PostAsJsonAsync($"/api/product-disposals/{draft.Id()}/complete", new { rowVersion = draft.RowVersion() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(80m, await OnHandAsync(client, sale.ProductId));
        Assert.Equal(("ProductDisposal", draft.Text("code"), -20m), ((await MovementsAsync(client, sale.ProductId, "?kind=Disposal")).GetProperty("items").EnumerateArray().Select(i => (i.Text("documentKind"), i.Text("documentCode"), i.Kg("quantityKg"))).Single()));

        var cancelled = await (await client.PostAsJsonAsync($"/api/product-disposals/{draft.Id()}/cancel", new { reason = "Ghi nhầm", rowVersion = completed.RowVersion() })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(100m, await OnHandAsync(client, sale.ProductId));
        Assert.Equal("Ghi nhầm", cancelled.Text("cancelReason"));
    }

    [Fact]
    public async Task A_completed_disposal_stays_editable_and_the_stock_follows_the_new_kg()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        var disposal = await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 20m, complete: true))).ReadAsync(HttpStatusCode.Created);

        await (await client.PutAsJsonAsync($"/api/product-disposals/{disposal.Id()}", new { zoneId = sale.ZoneId, reason = "Hàu chết", lines = new[] { new { productId = sale.ProductId, harvestLotId = lot.Id(), kg = 35m } }, rowVersion = disposal.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(65m, await OnHandAsync(client, sale.ProductId));
    }

    [Fact]
    public async Task A_disposal_line_names_a_lot_of_the_zone_that_was_split_into_the_product_with_kg_above_zero_and_a_completed_one_needs_a_reason()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        var other = await NewProductWithCageAsync(client);

        Assert.Equal("Line 1 lot", await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, sale.LotId, 5m, complete: true))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 1 lot", await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, other.Id(), lot.Id(), 5m, complete: true))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 1 kg", await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 0m, complete: true))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Reason", await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 5m, complete: true, reason: null))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_zone_manager_records_the_disposals_of_its_own_zone_only_and_the_accountant_only_reads()
    {
        var sale = await factory.SaleSetupAsync();
        var lot = await sale.Owner.Client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        var otherManager = await factory.ZoneManagerAsync((await factory.ZoneWithRaftAsync()).Zone);
        var accountant = await factory.AccountantAsync();

        var created = await (await sale.Manager.Client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 10m, complete: true))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NotFound, (await otherManager.GetAsync($"/api/product-disposals/{created.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherManager.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 10m, complete: true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/product-disposals/{created.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 10m, complete: true))).StatusCode);
    }

    [Fact]
    public async Task A_draft_is_removed_but_a_completed_disposal_is_never_removed_and_its_lot_cannot_be_removed()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        var draft = await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 10m, complete: false))).ReadAsync(HttpStatusCode.Created);
        var completed = await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 10m, complete: true))).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/product-disposals/{draft.Id()}")).StatusCode);
        await (await client.DeleteAsync($"/api/product-disposals/{completed.Id()}")).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        await (await factory.SendRemovalAsync(client, lot.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_lot_on_a_cancelled_disposal_is_still_not_removed()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        var disposal = await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 10m, complete: true))).ReadAsync(HttpStatusCode.Created);
        await (await client.PostAsJsonAsync($"/api/product-disposals/{disposal.Id()}/cancel", new { reason = "Ghi nhầm", rowVersion = disposal.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        await (await factory.SendRemovalAsync(client, lot.Id())).ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task An_invoice_line_on_a_lot_split_per_product_must_name_one_of_its_products()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var other = await NewProductWithCageAsync(client);
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));

        var response = await client.PostInvoiceAsync(Invoice(sale.CustomerId, [Line(other.Id(), lot.Id(), 5m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lot.Id()));

        Assert.Equal("Line 1 product", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Lines_are_not_added_to_a_lot_that_has_invoice_lines_and_a_product_with_sales_is_not_taken_off_a_lot()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, sale.LotId, 5m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), sale.LotId));
        var addLines = await factory.SendCorrectionAsync(client, sale.LotId, HarvestFields.OfLanes(1).WithProducts((sale.ProductId, 4, 100m)));
        Assert.Equal("Product lines", await addLines.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));

        var other = await NewProductWithCageAsync(client);
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        await client.SellAsync(Invoice(sale.CustomerId, [Line(sale.ProductId, lot.Id(), 5m)], Cash(0m, "PayLater", payLaterOwnerAgreed: true), lot.Id()));
        var dropSold = await factory.SendCorrectionAsync(client, lot.Id(), HarvestFields.OfLanes(3).WithProducts((other.Id(), 4, 100m)));

        Assert.Equal("Product lines", await dropSold.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_completed_disposal_of_a_product_deleted_since_can_still_be_edited_and_an_unchanged_correction_writes_no_new_rows()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var lot = await client.HarvestLotAsync(sale.RaftId, HarvestFields.OfLanes(3).WithProducts((sale.ProductId, 4, 100m)));
        var disposal = await (await client.PostAsJsonAsync("/api/product-disposals", Disposal(sale.ZoneId, sale.ProductId, lot.Id(), 20m, complete: true))).ReadAsync(HttpStatusCode.Created);
        await (await client.DeleteAsync($"/api/products/{sale.ProductId}")).ReadAsync(HttpStatusCode.OK);

        var edited = await client.PutAsJsonAsync($"/api/product-disposals/{disposal.Id()}", new { zoneId = sale.ZoneId, reason = "Hàu chết do mưa lớn", notes = "Ghi chú mới", lines = new[] { new { productId = sale.ProductId, harvestLotId = lot.Id(), kg = 20m } }, rowVersion = disposal.RowVersion() });
        await edited.ReadAsync(HttpStatusCode.OK);

        var rows = (await MovementsAsync(client, sale.ProductId, "?includeReversals=true")).GetProperty("items").GetArrayLength();
        Assert.Equal(2, rows);
    }
}
