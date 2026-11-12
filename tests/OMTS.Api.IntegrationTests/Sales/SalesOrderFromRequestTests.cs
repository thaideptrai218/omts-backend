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
/// An order made from a trader's purchase request: the Owner's form is prefilled from the request, saving it turns the request into Ordered (the order is App sourced) and tells the trader; a request that has its
/// order, is declined or was cancelled cannot be ordered again; a request accepted before orders existed (and not Fulfilled) can.
/// </summary>
public class SalesOrderFromRequestTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static Dictionary<string, object?> FromRequest(SaleSetup sale, TraderActor trader, JsonElement request, Guid unit, decimal quantity = 100m) =>
        OrderBody(trader.Profile.Id, sale.ProductId, unit, quantity, source: "App").WithField("purchaseRequestId", request.Id());

    [Fact]
    public async Task The_form_is_prefilled_from_the_request_and_saving_it_turns_the_request_into_ordered_and_links_the_two_by_code()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId, kg: 120m, pickupDate: Today.AddDays(3), note: "Fresh please");

        var draft = await sale.Owner.Client.GetFromJsonAsync<JsonElement>($"{Route}/request-draft/{request.Id()}");
        var saved = await sale.Owner.Client.CreateOrderAsync(FromRequest(sale, trader, request, unit, 120m));

        var line = Assert.Single(draft.GetProperty("lines").EnumerateArray());
        Assert.Equal((120m, unit, "App", trader.Profile.Id), (line.GetProperty("quantity").GetDecimal(), line.GetProperty("unitId").GetGuid(), draft.Text("source"), draft.GetProperty("customer").Id()));
        Assert.Equal("Fresh please", draft.Text("note"));
        var order = saved.GetProperty("order");
        Assert.Equal("App", order.Text("source"));
        Assert.Equal(request.Id(), order.GetProperty("purchaseRequest").Id());
        var staff = await sale.Owner.Client.ViewedByStaffAsync(request.Id());
        Assert.Equal((StatusIds.PurchaseRequest.Ordered, order.Text("code")), (staff.GetProperty("statusId").GetInt32(), staff.Text("salesOrderCode")));
        Assert.Equal(order.Text("code"), (await trader.Client.MineAsync(request.Id())).Text("salesOrderCode"));
    }

    [Fact]
    public async Task The_trader_is_told_that_the_request_became_an_order_with_the_pickup_time()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var body = FromRequest(sale, trader, request, unit).WithField("pickupAt", "2026-11-20T08:30:00+07:00");

        var order = (await sale.Owner.Client.CreateOrderAsync(body)).GetProperty("order");

        var notice = await factory.QueryAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.RecipientAccountId == trader.Account.Id && n.Type == NotificationTypes.PurchaseRequestResponse && n.MessageCode == "MSG-ORDER-04" && n.Channel == NotificationChannel.InApp)
            .Select(n => n.Body).SingleAsync());
        Assert.Contains($"Đã đặt hàng {order.Text("code")}, hẹn lấy 08:30 20/11/2026", notice);
    }

    [Fact]
    public async Task A_request_that_has_its_order_cannot_be_ordered_again()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var first = (await sale.Owner.Client.CreateOrderAsync(FromRequest(sale, trader, request, unit))).GetProperty("order");

        var again = await sale.Owner.Client.PostOrderAsync(FromRequest(sale, trader, request, unit));
        var draft = await sale.Owner.Client.GetAsync($"{Route}/request-draft/{request.Id()}");

        var body = await again.ProblemAsync(HttpStatusCode.Conflict, "MSG-ORDER-02");
        Assert.Equal(first.Id(), body.GetProperty("existingId").GetGuid());
        await draft.ProblemAsync(HttpStatusCode.Conflict, "MSG-ORDER-02");
    }

    [Fact]
    public async Task A_request_accepted_before_orders_existed_and_not_fulfilled_can_be_ordered()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId, kg: 100m);
        var accepted = await (await sale.Owner.Client.AcceptAsync(request, kg: 80m)).ReadAsync(HttpStatusCode.OK);

        var draft = await sale.Owner.Client.GetFromJsonAsync<JsonElement>($"{Route}/request-draft/{request.Id()}");
        await sale.Owner.Client.CreateOrderAsync(FromRequest(sale, trader, accepted, unit, 80m));

        // The quantity of the form is the kg the Owner accepted, not the kg asked.
        Assert.Equal(80m, draft.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(StatusIds.PurchaseRequest.Ordered, (await sale.Owner.Client.ViewedByStaffAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task A_declined_request_cannot_be_ordered()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var declined = await (await sale.Owner.Client.DeclineAsync(request)).ReadAsync(HttpStatusCode.OK);

        var response = await sale.Owner.Client.PostOrderAsync(FromRequest(sale, trader, declined, unit));

        await response.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    public async Task A_request_belongs_to_the_customer_of_the_order_and_only_the_owner_turns_it_into_an_order()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        var otherCustomer = FromRequest(sale, trader, request, unit).WithField("customerId", sale.CustomerId);

        var wrongCustomer = await sale.Owner.Client.PostOrderAsync(otherCustomer);
        var byManager = await sale.Manager.Client.PostOrderAsync(FromRequest(sale, trader, request, unit));

        Assert.Equal("Purchase request", await wrongCustomer.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        await byManager.ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, (await sale.Owner.Client.ViewedByStaffAsync(request.Id())).GetProperty("statusId").GetInt32());
    }

    [Fact]
    public async Task An_ordered_request_cannot_be_cancelled_by_its_trader()
    {
        var sale = await factory.SaleSetupAsync();
        var trader = await factory.TraderAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var request = await trader.Client.PlaceAsync(sale.ProductId);
        await sale.Owner.Client.CreateOrderAsync(FromRequest(sale, trader, request, unit));

        var mine = await trader.Client.MineAsync(request.Id());
        var cancel = await trader.Client.CancelAsync(mine);

        Assert.False(mine.GetProperty("canCancel").GetBoolean());
        await cancel.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }
}
