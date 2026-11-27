using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// Input VAT: a supply item has a rate (10% at first, KCT for seed), a receipt line copies it and may differ, the VAT goes into what the supplier is owed, into the debt and into the cost, a return gives
/// it back, a receipt made before input VAT keeps its figures, and the purchases report shows it.
/// </summary>
public class PurchaseInputVatTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Items = "/api/farming-input-items";

    private static Task<Guid> PhaoAsync(PurchasingWorld world, string? rate = null) =>
        world.Owner.CreateItemAsync("Material", "cái", vatRate: rate);

    private static async Task<JsonElement> ItemAsync(HttpClient owner, Guid itemId) => await owner.GetFromJsonAsync<JsonElement>($"{Items}/{itemId}");

    private static async Task<JsonElement> ReceiptAsync(HttpClient owner, JsonElement receipt) => await owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");

    [Fact]
    public async Task A_supply_item_starts_at_ten_percent_a_seed_item_is_always_KCT_and_the_list_filters_by_rate()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var phao = await (await owner.PostAsJsonAsync(Items, new { code = NewCode(), name = $"Phao {NewCode()}", group = "Material", unit = "cái" })).ReadAsync(HttpStatusCode.Created);
        var lowered = await (await owner.PostAsJsonAsync(Items, new { code = NewCode(), name = $"Lưới {NewCode()}", group = "Other", unit = "m", purchaseVatRate = "5" })).ReadAsync(HttpStatusCode.Created);
        var seed = await (await owner.PostAsJsonAsync(Items, new
        {
            code = NewCode(), name = $"Giống {NewCode()}", group = "Seed", unit = "con", purchaseVatRate = "10", productId = await owner.SeedProductAsync(),
        })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(("10", "5", "KCT"), (phao.GetProperty("purchaseVatRate").GetString(), lowered.GetProperty("purchaseVatRate").GetString(), seed.GetProperty("purchaseVatRate").GetString()));

        // An update that sends no rate keeps it; one that sends a rate changes it; a seed item stays KCT whatever is sent.
        var kept = await (await owner.PutAsJsonAsync($"{Items}/{lowered.Id()}", new
        {
            code = lowered.GetProperty("code").GetString(), name = lowered.GetProperty("name").GetString(), group = "Other", unit = "m", rowVersion = lowered.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("5", kept.GetProperty("purchaseVatRate").GetString());
        var changed = await (await owner.PutAsJsonAsync($"{Items}/{lowered.Id()}", new
        {
            code = lowered.GetProperty("code").GetString(), name = lowered.GetProperty("name").GetString(), group = "Other", unit = "m", purchaseVatRate = "0",
            rowVersion = kept.GetProperty("rowVersion").GetString(),
        })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("0", changed.GetProperty("purchaseVatRate").GetString());

        var ten = await owner.GetFromJsonAsync<JsonElement>($"{Items}/search?purchaseVatRate=10&pageSize=100");
        Assert.Contains(ten.GetProperty("items").EnumerateArray(), i => i.Id() == phao.Id());
        Assert.DoesNotContain(ten.GetProperty("items").EnumerateArray(), i => i.Id() == lowered.Id() || i.Id() == seed.Id());
        Assert.Equal("Purchase VAT rate", await (await owner.GetAsync($"{Items}/search?purchaseVatRate=12")).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_bulk_change_sets_the_rate_of_items_only_and_never_touches_a_receipt()
    {
        var world = await factory.WorldAsync();
        var (a, b) = (await PhaoAsync(world, "10"), await PhaoAsync(world, "5"));
        var receipt = await world.CompletedAsync(new LineBody(a, 1, 100_000));

        var done = await (await world.Owner.PostAsJsonAsync($"{Items}/purchase-vat-rate", new { itemIds = new[] { a, b }, purchaseVatRate = "8" })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal(2, done.GetProperty("updated").GetInt32());
        Assert.Equal(["8", "8"], new[] { await ItemAsync(world.Owner, a), await ItemAsync(world.Owner, b) }.Select(i => i.GetProperty("purchaseVatRate").GetString()));
        var line = (await ReceiptAsync(world.Owner, receipt)).LineViewOf(a);
        Assert.Equal(("10", 10_000m), (line.GetProperty("vatRate").GetString(), line.Money("vatAmount")));
        Assert.Equal(110_000m, await world.DebtAsync());

        // The route the contract names reaches the same action.
        await (await world.Owner.PostAsJsonAsync("/api/input-items/purchase-vat-rate", new { itemIds = new[] { a }, purchaseVatRate = "KCT" })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal("KCT", (await ItemAsync(world.Owner, a)).GetProperty("purchaseVatRate").GetString());
    }

    [Fact]
    public async Task The_bulk_change_refuses_a_seed_item_an_empty_list_and_an_unknown_item_with_the_field_itemIds()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);

        Assert.Equal("itemIds", await (await world.Owner.PostAsJsonAsync($"{Items}/purchase-vat-rate", new { itemIds = new[] { phao, world.Seed }, purchaseVatRate = "8" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("itemIds", await (await world.Owner.PostAsJsonAsync($"{Items}/purchase-vat-rate", new { itemIds = Array.Empty<Guid>(), purchaseVatRate = "8" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("itemIds", await (await world.Owner.PostAsJsonAsync($"{Items}/purchase-vat-rate", new { itemIds = new[] { phao, Guid.NewGuid() }, purchaseVatRate = "8" })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("purchaseVatRate", await (await world.Owner.PostAsJsonAsync($"{Items}/purchase-vat-rate", new { itemIds = new[] { phao } })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Owner.PostAsJsonAsync($"{Items}/purchase-vat-rate", new { itemIds = new[] { phao }, purchaseVatRate = "12" })).StatusCode);
        Assert.Equal("10", (await ItemAsync(world.Owner, phao)).GetProperty("purchaseVatRate").GetString());
    }

    [Fact]
    public async Task A_receipt_of_one_phao_at_100000_is_owed_110000_adds_110000_to_the_debt_and_costs_110000()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);

        var receipt = await world.CompletedAsync(new LineBody(phao, 1, 100_000));

        var line = receipt.LineViewOf(phao);
        Assert.Equal(("10", 10_000m), (line.GetProperty("vatRate").GetString(), line.Money("vatAmount")));
        Assert.Equal((100_000m, 10_000m, 110_000m), (receipt.Money("total"), receipt.Money("vatTotal"), receipt.Money("payable")));
        Assert.Equal(110_000m, await world.DebtAsync());
        var item = await ItemAsync(world.Owner, phao);
        Assert.Equal((110_000m, 110_000m), (item.Money("costPrice"), item.Money("lastPurchasePrice")));
        var posted = await factory.QueryAsync(db => db.InputStockMovements.Where(m => m.ItemId == phao && m.Kind == InputStockMovementKind.Receipt).Select(m => m.TransactionPrice).ToListAsync());
        Assert.Equal([110_000m], posted);
        // The list shows the same figures.
        var listed = (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts?code={receipt.GetProperty("code").GetString()}")).GetProperty("page").GetProperty("items")[0];
        Assert.Equal((10_000m, 110_000m), (listed.Money("vatTotal"), listed.Money("payable")));
    }

    [Fact]
    public async Task A_discount_on_the_receipt_takes_its_share_off_the_VAT()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);

        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(phao, 1, 100_000)) with { Complete = true, Discount = 10_000 });

        Assert.Equal((9_000m, 99_000m), (receipt.Money("vatTotal"), receipt.Money("payable")));
        Assert.Equal(99_000m, await world.DebtAsync());
    }

    [Fact]
    public async Task A_line_may_take_another_rate_than_its_item_and_the_item_keeps_its_own()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);

        var receipt = await world.CompletedAsync(new LineBody(phao, 1, 100_000, VatRate: "8"), new LineBody(await PhaoAsync(world, "5"), 1, 100_000, VatRate: "KCT"));

        Assert.Equal((8_000m, 208_000m), (receipt.Money("vatTotal"), receipt.Money("payable")));
        Assert.Equal("10", (await ItemAsync(world.Owner, phao)).GetProperty("purchaseVatRate").GetString());
        Assert.Equal(208_000m, await world.DebtAsync());
    }

    [Fact]
    public async Task A_seed_line_is_always_KCT_whatever_is_sent()
    {
        var world = await factory.WorldAsync();

        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 10, 1_000, VatRate: "10"));

        var line = receipt.LineViewOf(world.Seed);
        Assert.Equal(("KCT", 0m), (line.GetProperty("vatRate").GetString(), line.Money("vatAmount")));
        Assert.Equal(10_000m, await world.DebtAsync());
    }

    [Fact]
    public async Task A_return_gives_back_the_VAT_of_the_receipt_line_and_lowers_the_debt_by_it()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);
        var receipt = await world.CompletedAsync(new LineBody(phao, 4, 100_000));
        Assert.Equal(440_000m, await world.DebtAsync());

        var after = await world.ReturnAsync(receipt.Id(), receipt.LineOf(phao), 1);

        var back = after.GetProperty("returns")[0];
        Assert.Equal((100_000m, 10_000m, 110_000m), (back.Money("returnValue"), back.Money("vatRefund"), back.Money("netValue")));
        Assert.Equal((110_000m, 440_000m), (after.Money("returned"), after.Money("payable")));
        Assert.Equal(330_000m, await world.DebtAsync());

        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-returns/{back.Id()}");
        var line = view.GetProperty("lines")[0];
        Assert.Equal(("10", 10_000m), (line.GetProperty("vatRate").GetString(), line.Money("vatAmount")));
        Assert.Equal((110_000m, 10_000m), (view.Money("netValue"), view.Money("vatRefund")));
    }

    [Fact]
    public async Task Money_paid_back_for_a_return_may_be_up_to_its_net_value_with_the_VAT()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);
        var receipt = await world.CompletedAsync(new LineBody(phao, 1, 100_000));
        await world.PayAsync(110_000);

        var refused = await world.Owner.PostAsJsonAsync($"/api/purchase-returns", new
        {
            receiptId = receipt.Id(), reason = "Cracked", complete = true, lines = new[] { new { receiptLineId = receipt.LineOf(phao), quantity = 1m } },
            refund = new { amount = 110_001m, method = "Cash" },
        });
        Assert.Equal("Refund amount", await refused.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var accepted = await world.Owner.PostAsJsonAsync($"/api/purchase-returns", new
        {
            receiptId = receipt.Id(), reason = "Cracked", complete = true, lines = new[] { new { receiptLineId = receipt.LineOf(phao), quantity = 1m } },
            refund = new { amount = 110_000m, method = "Cash" },
        });
        await accepted.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(0m, await world.DebtAsync());
    }

    [Fact]
    public async Task Editing_a_completed_receipt_to_another_rate_moves_its_VAT_the_debt_and_the_price_posted()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);
        var receipt = await world.CompletedAsync(new LineBody(phao, 1, 100_000));
        var lineId = receipt.LineOf(phao);

        var body = new ReceiptBody
        {
            SupplierId = world.SupplierId, ReceiptDate = null, Complete = true, Lines = [new LineBody(phao, 1, 100_000, LineId: lineId, VatRate: "8")],
        };
        var edited = await (await world.Owner.EditAsync(await ReceiptAsync(world.Owner, receipt), body)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((8_000m, 108_000m), (edited.Money("vatTotal"), edited.Money("payable")));
        Assert.Equal(108_000m, await world.DebtAsync());
        Assert.Equal(108_000m, (await ItemAsync(world.Owner, phao)).Money("lastPurchasePrice"));
        var unreversed = await factory.QueryAsync(db => db.InputStockMovements.Where(m => m.ItemId == phao && m.Kind == InputStockMovementKind.Receipt).OrderBy(m => m.Sequence).Select(m => m.TransactionPrice).ToListAsync());
        Assert.Equal([110_000m, 108_000m], unreversed);

        // A rate left out keeps the rate the line has.
        var again = await (await world.Owner.EditAsync(edited, body with { Lines = [new LineBody(phao, 2, 100_000, LineId: lineId)] })).ReadAsync(HttpStatusCode.OK);
        Assert.Equal((16_000m, 216_000m), (again.Money("vatTotal"), again.Money("payable")));
    }

    [Fact]
    public async Task A_receipt_made_before_input_VAT_keeps_its_figures_also_after_an_edit_that_names_no_rate()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);
        var receipt = await world.CompletedAsync(new LineBody(phao, 1, 100_000));
        var lineId = receipt.LineOf(phao);
        // What a row of the migration looks like: no rate, no VAT.
        await factory.UpdateAsync<PurchaseReceiptLine>(lineId, l => (l.VatRate, l.VatAmount) = (null, 0m));

        var old = await ReceiptAsync(world.Owner, receipt);

        Assert.Equal(JsonValueKind.Null, old.LineViewOf(phao).GetProperty("vatRate").ValueKind);
        Assert.Equal((0m, 100_000m), (old.Money("vatTotal"), old.Money("payable")));
        Assert.Equal(100_000m, await world.DebtAsync());

        await (await world.Owner.EditLineAsync(receipt.Id(), lineId, quantity: 2)).ReadAsync(HttpStatusCode.OK);
        var edited = await ReceiptAsync(world.Owner, receipt);
        Assert.Equal(JsonValueKind.Null, edited.LineViewOf(phao).GetProperty("vatRate").ValueKind);
        Assert.Equal((200_000m, 0m, 200_000m), (edited.Money("total"), edited.Money("vatTotal"), edited.Money("payable")));
        Assert.Equal(200_000m, await world.DebtAsync());
    }

    [Fact]
    public async Task The_purchases_report_shows_the_goods_the_VAT_and_what_is_owed()
    {
        var world = await factory.WorldAsync();
        var phao = await PhaoAsync(world);
        var receipt = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(phao, 1, 100_000)) with { Complete = true, Discount = 10_000 });
        await world.ReturnAsync(receipt.Id(), receipt.LineOf(phao), 1);
        var other = await world.CompletedAsync(new LineBody(phao, 2, 50_000));

        var report = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/reports/purchases?supplierIds={world.SupplierId}");

        var totals = report.GetProperty("totals");
        // Goods: 90,000 on the first receipt (100,000 less 10,000) and 100,000 on the second; VAT 9,000 and 10,000. The return is worth the line less its share of the discount (90,000) and gives back 9,000 VAT.
        Assert.Equal(90_000m + 100_000m, totals.Money("receivedValue"));
        Assert.Equal(90_000m, totals.Money("returnedValue"));
        Assert.Equal(9_000m + 10_000m, totals.Money("receivedVat"));
        Assert.Equal(10_000m, totals.Money("vatAmount"));
        Assert.Equal(totals.Money("receivedValue") - totals.Money("returnedValue"), totals.Money("goodsValue"));
        Assert.Equal(totals.Money("goodsValue") + totals.Money("vatAmount"), totals.Money("netValue"));
        var group = Assert.Single(report.GetProperty("groups").EnumerateArray());
        Assert.Equal((totals.Money("netValue"), totals.Money("vatAmount")), (group.Money("netValue"), group.Money("vatAmount")));
        Assert.Equal(totals.Money("netValue"), await world.DebtAsync());
    }
}
