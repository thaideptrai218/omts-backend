using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// BR-PURCHASE-02, PM 03/11: a quantity has at most 2 decimals (MSG-GEN-01 with <c>field</c>), each line is rounded half up
/// to the dong on every path that computes it, and rows saved before the rule are refused on completing.
/// </summary>
public class PurchaseReceiptMoneyRoundingTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private const string Receipts = "/api/purchase-receipts";

    private Task SetStoredQuantityAsync(Guid lineId, decimal quantity) =>
        // A row saved before the rule holds the quantity as typed (EnteredQuantity, which the precision rule reads) and in base units (Quantity); the unit factor of these rows is 1.
        factory.UpdateAsync<PurchaseReceiptLine>(lineId, l => (l.Quantity, l.EnteredQuantity) = (quantity, quantity));

    [Fact]
    public async Task A_quantity_with_three_decimals_is_refused_with_MSG_GEN_01_and_the_field_on_every_save()
    {
        var world = await factory.WorldAsync();
        var valid = new LineBody(world.Other, 1, 1_000);
        var tooPrecise = new LineBody(world.Other, 12.335m, 45_500);

        Assert.Equal("Line 2", await (await world.Owner.PostReceiptAsync(world.Body(valid, tooPrecise))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal("Line 1", await (await world.Owner.PostReceiptAsync(world.Body(tooPrecise) with { Complete = true })).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(0, await factory.QueryAsync(db => db.PurchaseReceipts.CountAsync(r => r.SupplierId == world.SupplierId)));

        var draft = await world.Owner.CreateReceiptAsync(world.Body(valid));
        Assert.Equal("Line 1", await (await world.Owner.EditAsync(draft, world.Body(tooPrecise))).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task Two_decimals_and_trailing_zeros_are_accepted_and_a_half_rounds_up_on_every_save()
    {
        var world = await factory.WorldAsync();

        // 12.5 × 45,501 = 568,762.5 (to even: 568,762); 1.230 is 1.23; 1.23 × 1,000 = 1,230.
        var draft = await world.Owner.CreateReceiptAsync(world.Body(new LineBody(world.Other, 12.5m, 45_501), new LineBody(world.Material, 1.230m, 1_000)));

        Assert.Equal([568_763m, 1_230m], draft.GetProperty("lines").EnumerateArray().Select(l => l.Money("lineAmount")));
        Assert.Equal(569_993m, draft.Money("total"));

        // 0.5 × 1 three times: 1 + 1 + 1, never the 2 of the rounded sum, also when a Draft is edited and completed.
        var completed = await (await world.Owner.EditAsync(draft, world.Body(new LineBody(world.Other, 0.5m, 1), new LineBody(world.Material, 0.5m, 1), new LineBody(world.Other, 0.5m, 1)) with { Complete = true }))
            .ReadAsync(HttpStatusCode.OK);
        Assert.Equal(3m, completed.Money("total"));
    }

}
