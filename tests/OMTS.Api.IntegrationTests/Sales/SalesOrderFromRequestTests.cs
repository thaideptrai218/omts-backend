using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// A purchase request sent from the Mini App makes a Draft order (App source) for the customer at once and stays Awaiting response: the trader sees the order code, the Owner cannot order it again, and the trader
/// cancelling the request cancels the Draft order too.
/// </summary>
public class SalesOrderFromRequestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Dictionary<string, object?> FromRequest(SaleSetup sale, TraderActor trader, JsonElement request, Guid unit, decimal quantity = 100m) =>
        OrderBody(trader.Profile.Id, sale.ProductId, unit, quantity, source: "App").WithField("purchaseRequestId", request.Id());

    [Fact]
    public async Task Placing_a_request_makes_a_draft_order_of_the_customer_and_leaves_the_request_awaiting_response()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);

        var request = await trader.Client.PlaceAsync(sale.ProductId, cages: 3, pickupDate: Today.AddDays(3), note: "Fresh please");

        var order = await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.PurchaseRequestId == request.Id()));
        Assert.Equal((StatusIds.SalesOrder.Draft, SalesOrderSource.App, trader.Profile.Id, "Fresh please"), (order.StatusId, order.Source, order.CustomerId, order.Note));
        var line = Assert.Single(order.Lines);
        Assert.Equal((unit, sale.ProductId), (line.UnitId, line.ProductId));
        Assert.True(line.Quantity > 0m);
        var staff = await sale.Owner.Client.ViewedByStaffAsync(request.Id());
        Assert.Equal((StatusIds.PurchaseRequest.AwaitingResponse, order.Code), (staff.GetProperty("statusId").GetInt32(), staff.Text("salesOrderCode")));
        Assert.Equal(order.Code, (await trader.Client.MineAsync(request.Id())).Text("salesOrderCode"));
    }

    [Fact]
    public async Task A_request_that_has_its_order_cannot_be_ordered_again()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var existing = await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().Where(o => o.PurchaseRequestId == request.Id()).Select(o => o.Id).SingleAsync());

        var again = await sale.Owner.Client.PostOrderAsync(FromRequest(sale, trader, request, unit));
        var draft = await sale.Owner.Client.GetAsync($"{Route}/request-draft/{request.Id()}");

        var body = await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-ORDER-02");
        Assert.Equal(existing, body.GetProperty("existingId").GetGuid());
        await draft.ProblemAsync(HttpStatusCode.Conflict, "MSG-ORDER-02");
    }

    [Fact]
    public async Task Cancelling_the_request_cancels_its_draft_order()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(sale.ProductId);

        var mine = await trader.Client.MineAsync(request.Id());
        var cancel = await trader.Client.CancelAsync(mine, "Changed my mind");

        Assert.True(mine.GetProperty("canCancel").GetBoolean());
        await cancel.ReadAsync(HttpStatusCode.OK);
        var order = await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().SingleAsync(o => o.PurchaseRequestId == request.Id()));
        Assert.Equal((StatusIds.SalesOrder.Cancelled, "Changed my mind"), (order.StatusId, order.CancelReason));
    }
}
