using System.Net;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Purchasing;

/// <summary>
/// QA of OMTS-216 PR 0.2 on the split of old MSG59 (PURCHASE-01, PURCHASE-02.0.E1): an invalid receipt line or a
/// missing return quantity is MSG-GEN-01 naming the line, a return quantity not greater than 0 is MSG-PURCHASE-03
/// with the filled-in limit; every one names MSG59 as its legacy code.
/// </summary>
public class PurchasingMessageCodeQaTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Invalid_receipt_line_is_msg_gen_01_naming_the_line_with_legacy_msg59()
    {
        var world = await factory.WorldAsync();

        var refused = await (await world.Owner.PostReceiptAsync(world.Body(new LineBody(world.Other, 1, 10_000), new LineBody(world.Other, 0, 10_000))))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG59");

        Assert.Equal("Line 2 is missing or invalid. Please check and enter it again.", refused.Detail());
        Assert.Equal("Line 2", refused.Str("field"));
    }

    [Fact]
    public async Task Return_quantity_zero_is_msg_purchase_03_with_the_limit_and_a_missing_one_msg_gen_01_both_legacy_msg59()
    {
        var world = await factory.WorldAsync();
        var receipt = await world.CompletedAsync(0, 0, new LineBody(world.Other, 4, 25_000));
        var line = receipt.LineOf(world.Other);
        var body = new ReturnBody { DebtReductionAmount = 25_000, RefundAmount = 0 };

        var zero = await (await world.Owner.PostReturnAsync(receipt.Id(), body with { Lines = [new ReturnLineBody(line, 0)] }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-PURCHASE-03", "MSG59");
        var missing = await (await world.Owner.PostReturnAsync(receipt.Id(), body with { Lines = [new ReturnLineBody(line, null)] }))
            .SrsProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-01", "MSG59");

        var itemName = await factory.QueryAsync(db => db.FarmingInputItems.Where(i => i.Id == world.Other).Select(i => i.Name).SingleAsync());
        Assert.Equal($"{itemName}: enter a quantity greater than 0 and at most 4 bag. Received 4 bag, already returned 0 bag.", zero.Detail());
        Assert.Equal(("Line 1", 4m), (zero.Str("field"), zero.GetProperty("limit").GetDecimal()));
        Assert.Equal("Line 1 is missing or invalid. Please check and enter it again.", missing.Detail());
    }
}
