using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Domain.Enums;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// A purchase request sent from the Mini App makes a Draft order (App source) for the customer at once and stays Awaiting response: the trader sees the order code, the Owner cannot order it again, and the trader
/// cancelling the request cancels the Draft order too. The Owner confirming the Draft order makes the request Ordered, after which the trader cannot cancel it; the Owner declining the request cancels the Draft order.
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

    [Fact]
    public async Task Confirming_the_draft_order_makes_the_request_ordered_and_the_trader_can_no_longer_cancel_it()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var mine = await trader.Client.MineAsync(request.Id());
        var order = await sale.Owner.Client.GetOrderAsync(await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().Where(o => o.PurchaseRequestId == request.Id()).Select(o => o.Id).SingleAsync()));
        var confirmed = OrderBody(trader.Profile.Id, sale.ProductId, unit, quantity: 40m, source: "App").WithField("rowVersion", order.RowVersion());

        await (await sale.Owner.Client.PutAsJsonAsync($"{Route}/{order.Id()}", confirmed)).ReadAsync(HttpStatusCode.OK);

        var saved = await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id()));
        var ordered = await factory.QueryAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id()));
        Assert.Equal((StatusIds.SalesOrder.Draft, StatusIds.PurchaseRequest.Ordered), (saved.StatusId, ordered.StatusId));
        var changes = await factory.AuditRowsAsync(request.Id());
        Assert.Contains(changes, a => a.Action == "PurchaseRequest.StatusChanged");
        Assert.Contains(await factory.NotificationRowsAsync(request.Id()), n => n.Type == NotificationTypes.PurchaseRequestResponse);
        var cancel = await trader.Client.CancelAsync(mine, "Changed my mind");
        await cancel.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
        var after = await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id()));
        Assert.Equal(StatusIds.SalesOrder.Draft, after.StatusId);
        Assert.False((await trader.Client.MineAsync(request.Id())).GetProperty("canCancel").GetBoolean());
    }

    [Fact]
    public async Task Declining_the_request_cancels_its_draft_order_with_the_same_reason()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var staff = await sale.Owner.Client.ViewedByStaffAsync(request.Id());

        await (await sale.Owner.Client.DeclineAsync(staff, "Not enough oysters this week")).ReadAsync(HttpStatusCode.OK);

        var order = await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().SingleAsync(o => o.PurchaseRequestId == request.Id()));
        var declined = await factory.QueryAsync(db => db.PurchaseRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id()));
        Assert.Equal((StatusIds.SalesOrder.Cancelled, "Not enough oysters this week", StatusIds.PurchaseRequest.Declined), (order.StatusId, order.CancelReason, declined.StatusId));
    }
}
