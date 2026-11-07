using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Purchasing.PurchasingTestKit;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// PURCHASE-02.0, BR-PURCHASE-07: returnable quantity (received − seeded − returned), amounts at the unit prices of the receipt, and
/// the checks on the fields. What a saved return looks like, its cancellation and the Owner's approval have their own classes.
/// </summary>
public class PurchaseReturnRulesTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    // Seed 100 kg × 9,000 (total 900,000), 300,000 paid: debt 600,000.
    private async Task<(PurchasingWorld World, JsonElement Receipt, Guid SeedLine)> SeedReceiptAsync()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.PaidAsync(300_000, new LineBody(world.Seed, 100, 9_000));
        return (world, receipt, receipt.LineOf(world.Seed));
    }

    private static ReturnBody Return(Guid lineId, decimal? quantity) => new() { Lines = [new ReturnLineBody(lineId, quantity)] };

    [Fact]
    public async Task Cumulative_returns_stop_at_received_minus_seeded_and_each_lowers_the_debt_by_its_amount()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        await factory.SeedAsync(world.Raft.Id, line, 30);
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal((30m, 70m), (view.LineViewOf(world.Seed).Money("seeded"), view.LineViewOf(world.Seed).Money("returnable")));

        var first = await world.ReturnAsync(receipt.Id(), line, 50);
        Assert.Equal((450_000m, 20m), (first.Money("returned"), first.LineViewOf(world.Seed).Money("returnable")));
        Assert.Equal(150_000, await world.DebtAsync());

        // Past the debt the cooperative has paid in advance: nothing limits a return but its quantity (the money limits are gone).
        var second = await world.ReturnAsync(receipt.Id(), line, 20);
        Assert.Equal((630_000m, 0m, 70m), (second.Money("returned"), second.LineViewOf(world.Seed).Money("returnable"), second.LineViewOf(world.Seed).Money("returnedQuantity")));
        Assert.Equal(-30_000, await world.DebtAsync());

        var problem = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 1))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.EndsWith(
            ": enter a quantity greater than 0 and at most 0 kg. Received 100 kg, seeded 30 kg, already returned 70 kg.",
            problem.GetProperty("detail").GetString());
        Assert.Equal(("Line 1", line, 0m, "kg", 100m, 30m, 70m), (problem.GetProperty("field").GetString(), problem.GetProperty("receiptLineId").GetGuid(), problem.Money("maxQuantity"),
            problem.GetProperty("unit").GetString(), problem.Money("received"), problem.Money("seeded"), problem.Money("returned")));
        Assert.Equal(2, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));

        // Money the supplier gives back is a PaidBack payment now: the statement has no refund entry, and the debt rises by it.
        var statement = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/supplier-debts/{world.SupplierId}/statement");
        Assert.Equal(
            [("Receipt", 900_000m), ("Return", -450_000m), ("Return", -180_000m), ("Payment", -300_000m)],
            statement.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("kind").GetString(), e.Money("amount"))).ToList());
        Assert.Equal(-30_000m, statement.Money("closingDebt"));
        await world.PayAsync(30_000, kind: "PaidBack");
        Assert.Equal(0, await world.DebtAsync());
    }

    // BR-PURCHASE-02 (PM 03/11): each line half up to the dong, the total the sum of the rounded lines, never to thousands.
    [Fact]
    public async Task The_amount_of_a_return_is_each_line_half_up_to_the_dong_and_the_total_the_sum_of_the_lines()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333), new LineBody(world.Material, 0.5m, 3));
        Assert.Equal(502m, receipt.Money("total"));

        var returned = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            Lines = [new ReturnLineBody(receipt.LineOf(world.Other), 0.5m), new ReturnLineBody(receipt.LineOf(world.Material), 0.5m)],
        })).ReadAsync(HttpStatusCode.Created);

        // 166.5 → 167 and 1.5 → 2, where the sum of the products, 168, would round to 168.
        Assert.Equal(169m, returned.Money("returned"));
        var detail = await (await world.Owner.GetAsync($"/api/purchase-returns/{returned.GetProperty("returns")[0].Id()}")).ReadAsync(HttpStatusCode.OK);
        Assert.Equal(169m, detail.Money("returnValue"));
        Assert.Equal([167m, 2m], detail.GetProperty("lines").EnumerateArray().Select(l => l.Money("returnValue")).Order().Reverse());
        Assert.Equal(502 - 169, await world.DebtAsync());
    }

    // Each part rounds by itself and no part is adjusted (PM 03/11), so the parts of a line can add up to a few dong more or less than the line.
    [Fact]
    public async Task Parts_of_a_line_round_one_by_one_and_may_pass_the_line_by_a_dong()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Other, 1.5m, 333));
        var line = receipt.LineOf(world.Other);

        JsonElement last = default;
        for (var part = 0; part < 3; part++)
        {
            last = await world.ReturnAsync(receipt.Id(), line, 0.5m);
        }

        Assert.Equal((500m, 501m, 0m), (last.Money("total"), last.Money("returned"), last.LineViewOf(world.Other).Money("returnable")));
        Assert.Equal(-1, await world.DebtAsync());
    }

    [Theory]
    [InlineData(1.555, "MSG-GEN-01")]
    [InlineData(0.001, "MSG-GEN-01")]
    [InlineData(0, "MSG-PURCHASE-03")]
    [InlineData(-1, "MSG-PURCHASE-03")]
    public async Task A_quantity_has_at_most_two_decimals_and_is_above_zero(double quantity, string code)
    {
        var (world, receipt, line) = await SeedReceiptAsync();

        Assert.Equal("Line 1", await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, (decimal)quantity))).FieldAsync(HttpStatusCode.BadRequest, code));

        var accepted = await world.ReturnAsync(receipt.Id(), line, 1.55m);
        Assert.Equal(13_950m, accepted.Money("returned"));
    }

    [Fact]
    public async Task Returned_quantity_above_returnable_names_the_line_and_the_limit_with_its_unit()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        await factory.SeedAsync(world.Raft.Id, line, 30);
        var problem = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 70.5m))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.EndsWith(
            ": enter a quantity greater than 0 and at most 70 kg. Received 100 kg, seeded 30 kg, already returned 0 kg.",
            problem.GetProperty("detail").GetString());
        Assert.Equal("MSG59", problem.GetProperty("legacyCode").GetString());
        Assert.Equal(70, problem.Money("maxQuantity"));
    }

    [Fact]
    public async Task A_seed_lot_seeded_in_whole_leaves_nothing_to_return()
    {
        var (world, receipt, line) = await SeedReceiptAsync();
        await factory.SeedAsync(world.Raft.Id, line, 100);
        var view = await world.Owner.GetFromJsonAsync<JsonElement>($"/api/purchase-receipts/{receipt.Id()}");
        Assert.Equal((100m, 0m), (view.LineViewOf(world.Seed).Money("seeded"), view.LineViewOf(world.Seed).Money("returnable")));
        var problem = await (await world.Owner.PostReturnAsync(receipt.Id(), Return(line, 1))).ProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03");
        Assert.EndsWith("at most 0 kg. Received 100 kg, seeded 100 kg, already returned 0 kg.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Non_seed_lines_are_never_seeded_and_a_material_return_lowers_the_raft_material_cost()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(world.MaterialForRaft(10, 50_000), new LineBody(world.Other, 4, 25_000));
        Assert.Equal(500_000, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));

        var returned = await (await world.Owner.PostReturnAsync(receipt.Id(), new ReturnBody
        {
            Lines = [new ReturnLineBody(receipt.LineOf(world.Material), 10), new ReturnLineBody(receipt.LineOf(world.Other), 4)],
        })).ReadAsync(HttpStatusCode.Created);

        Assert.Equal(600_000m, returned.Money("returned"));
        Assert.All(returned.GetProperty("lines").EnumerateArray(), l => Assert.Equal(0m, l.Money("seeded")));
        Assert.Equal(0, (await world.Owner.GetFromJsonAsync<JsonElement>($"/api/rafts/{world.Raft.Id}")).Money("materialCost"));
        Assert.Equal(0, await world.DebtAsync());
    }

    [Fact]
    public async Task Reason_and_lines_are_checked_with_MSG_GEN_01_and_a_missing_quantity_too()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(new LineBody(world.Seed, 100, 10_000));
        var line = receipt.LineOf(world.Seed);
        var otherReceipt = await world.CompletedAsync(new LineBody(world.Other, 1, 1000));
        var valid = Return(line, 1);

        foreach (var (body, field) in new (ReturnBody, string)[]
        {
            (valid with { Reason = null }, "Reason"),
            (valid with { Reason = "  " }, "Reason"),
            (valid with { Reason = new string('r', 1001) }, "Reason"),
            (valid with { Lines = [] }, "Lines"),
            (valid with { Lines = null }, "Lines"),
            (valid with { Lines = [new ReturnLineBody(otherReceipt.LineOf(world.Other), 1)] }, "Line 1"),
            (valid with { Lines = [new ReturnLineBody(null, 1)] }, "Line 1"),
            (valid with { Lines = [new ReturnLineBody(line, 1), new ReturnLineBody(line, 1)] }, "Lines"),
            (Return(line, null), "Line 1"),
        })
        {
            Assert.Equal(field, await (await world.Owner.PostReturnAsync(receipt.Id(), body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        }

        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReturns.CountAsync(r => r.ReceiptId == receipt.Id())));
    }

}
