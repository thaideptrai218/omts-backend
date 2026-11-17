using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Constants;
using static OMTS.Api.IntegrationTests.Sales.SalesInvoiceTestKit;
using static OMTS.Api.IntegrationTests.Sales.SalesOrderTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>
/// The orders of goods through the API: saved with their advance in one save, the right of each role and zone, codes the user types, a repeated deduplication key, an advance that is reserved for its order,
/// and a cancel that leaves the advance as the customer's credit.
/// </summary>
public class SalesOrderApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task An_order_with_a_cash_advance_is_saved_as_a_draft_and_its_advance_is_a_payment_awaiting_confirmation_that_names_the_order()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var body = OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 20m).WithField("advance", new { amount = 500_000, method = "Cash" });

        var saved = await client.CreateOrderAsync(body);

        var order = saved.GetProperty("order");
        Assert.Equal((StatusIds.SalesOrder.Draft, 600_000m, 500_000m), (order.GetProperty("statusId").GetInt32(), order.Money("total"), order.Money("customerPaid")));
        Assert.Matches("^DH[0-9]{6}$", order.Text("code"));
        var advance = Assert.Single(await factory.AdvancesOfOrderAsync(order.Id()));
        Assert.Equal((StatusIds.CustomerPayment.AwaitingConfirmation, 500_000m, sale.CustomerId), (advance.StatusId, advance.Amount, advance.CustomerId));
        Assert.Equal(advance.Id, Assert.Single(order.GetProperty("advances").EnumerateArray()).GetProperty("paymentId").GetGuid());
        Assert.Contains("Edit", order.AllowedActions());
        Assert.Contains("AddAdvance", order.AllowedActions());
    }

    [Fact]
    public async Task The_advance_is_reserved_for_the_order_it_lowers_the_debt_and_is_listed_apart_on_the_debt_screens()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit).WithField("advance", new { amount = 200_000, method = "Cash" }))).GetProperty("order");

        var tab = await client.GetFromJsonAsync<JsonElement>($"/api/customer-debts/{sale.CustomerId}");

        Assert.Equal(-200_000m, tab.Money("debt"));
        var reserved = Assert.Single(tab.GetProperty("reservedAdvances").EnumerateArray());
        Assert.Equal((order.Id(), order.Text("code"), 200_000m), (reserved.GetProperty("salesOrderId").GetGuid(), reserved.Text("salesOrderCode"), reserved.Money("amount")));
    }

    [Fact]
    public async Task A_buyer_with_no_profile_pays_no_advance()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var body = OrderBody(null, sale.ProductId, unit, source: "AtPort").WithField("advance", new { amount = 100_000, method = "Cash" });

        var field = await (await client.PostOrderAsync(body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        Assert.Equal("Advance", field);
    }

    [Fact]
    public async Task An_advance_above_the_total_of_the_order_is_refused_and_nothing_is_saved()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var code = $"DH-{NewCode()}";
        var body = OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 2m).WithField("code", code).WithField("advance", new { amount = 100_000, method = "Cash" });

        var field = await (await client.PostOrderAsync(body)).FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01");

        Assert.Equal("Advance amount", field);
        Assert.Equal(0, await factory.OrderCountAsync(code));
    }

    [Fact]
    public async Task A_second_advance_added_later_cannot_take_the_advances_above_the_total()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m).WithField("advance", new { amount = 200_000, method = "Cash" }))).GetProperty("order");

        var tooMuch = await client.PostAsJsonAsync($"{Route}/{order.Id()}/advances", new { amount = 150_000, method = "Cash" });
        var fits = await client.PostAsJsonAsync($"{Route}/{order.Id()}/advances", new { amount = 100_000, method = "Cash" });

        Assert.Equal("Advance amount", await tooMuch.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var added = await fits.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(300_000m, added.GetProperty("order").Money("customerPaid"));
        Assert.Equal(2, (await factory.AdvancesOfOrderAsync(order.Id())).Count);
    }

    [Fact]
    public async Task An_order_has_no_zone_a_zone_manager_takes_a_buyer_at_the_port_and_a_zone_sent_is_ignored()
    {
        var sale = await factory.SaleSetupAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var otherZone = (await Farming.FarmingTestKit.CreateZoneAsync(sale.Owner.Client)).Id();

        var created = await sale.Manager.Client.CreateOrderAsync(OrderBody(null, sale.ProductId, unit, source: "AtPort").WithField("zoneId", otherZone));

        var order = created.GetProperty("order");
        Assert.Equal("AtPort", order.Text("source"));
        Assert.False(order.TryGetProperty("zone", out _));
        var orderId = order.Id();
        Assert.Null(await factory.QueryAsync(db => db.SalesOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.ZoneId).SingleAsync()));
        var list = await sale.Manager.Client.GetFromJsonAsync<JsonElement>($"{Route}?pageSize=50&zoneId={otherZone}");
        Assert.Contains(order.Id(), list.GetProperty("items").EnumerateArray().Select(i => i.Id()));
    }

    [Fact]
    public async Task A_zone_manager_takes_the_buyers_at_the_port_only()
    {
        var sale = await factory.SaleSetupAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);

        var response = await sale.Manager.Client.PostOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, source: "Phone"));

        Assert.Equal("Source", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task A_price_other_than_the_list_price_asks_the_owner_of_a_zone_manager_and_then_goes_through()
    {
        var sale = await factory.SaleSetupAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var body = OrderBody(null, sale.ProductId, unit, source: "AtPort", unitPrice: ListPrice - 5_000m);

        var question = await (await sale.Manager.Client.PostOrderAsync(body)).QuestionAsync();
        body["lines"] = new object[] { new { productId = sale.ProductId, unitId = unit, quantity = 20m, unitPrice = ListPrice - 5_000m, priceDecidedByOwner = true } };
        var created = await sale.Manager.Client.CreateOrderAsync(body);

        Assert.Equal(("MSG-SALE-10", "lines[0].priceDecidedByOwner"), (question.Code, question.Field));
        Assert.Equal(500_000m, created.GetProperty("order").Money("total"));
    }

    [Fact]
    public async Task A_code_the_user_types_is_kept_and_a_second_order_with_it_in_any_letter_case_is_refused_naming_the_field()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var code = $"DH-{NewCode()}";

        var first = await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit).WithField("code", code));
        var second = await client.PostOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit).WithField("code", code.ToLowerInvariant()));

        Assert.Equal(code, first.GetProperty("order").Text("code"));
        Assert.Equal("Code", await second.FieldAsync(HttpStatusCode.Conflict, "MSG-GEN-02"));
        Assert.Equal(1, await factory.OrderCountAsync(code));
    }

    [Fact]
    public async Task A_repeated_deduplication_key_returns_the_first_order_and_creates_nothing()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var body = OrderBody(sale.CustomerId, sale.ProductId, unit).WithField("deduplicationKey", $"order-{Guid.NewGuid()}").WithField("advance", new { amount = 100_000, method = "Cash" });

        var first = await client.CreateOrderAsync(body);
        var again = await client.CreateOrderAsync(body);

        Assert.Equal(first.GetProperty("order").Id(), again.GetProperty("order").Id());
        Assert.Single(await factory.AdvancesOfOrderAsync(first.GetProperty("order").Id()));
    }

    [Fact]
    public async Task Cancelling_an_order_leaves_its_advance_as_it_was_and_hands_it_to_the_customer_as_credit()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit).WithField("advance", new { amount = 500_000, method = "Cash" }))).GetProperty("order");

        var cancelled = await (await client.PostAsJsonAsync($"{Route}/{order.Id()}/cancel", new { reason = "The buyer changed his mind", rowVersion = order.RowVersion() })).ReadAsync(HttpStatusCode.OK);

        Assert.Equal((StatusIds.SalesOrder.Cancelled, 500_000m), (cancelled.GetProperty("order").GetProperty("statusId").GetInt32(), cancelled.Money("creditLeft")));
        var advance = Assert.Single(await factory.AdvancesOfOrderAsync(order.Id()));
        Assert.Equal((StatusIds.CustomerPayment.AwaitingConfirmation, 500_000m), (advance.StatusId, advance.Amount));
        // No order holds it now: the debt tab lists no reserved advance.
        var tab = await client.GetFromJsonAsync<JsonElement>($"/api/customer-debts/{sale.CustomerId}");
        Assert.Empty(tab.GetProperty("reservedAdvances").EnumerateArray());
        Assert.Equal(-500_000m, tab.Money("debt"));
    }

    [Fact]
    public async Task A_cancel_needs_a_reason_and_is_the_owners_a_zone_manager_may_finish_and_change_but_not_cancel()
    {
        var sale = await factory.SaleSetupAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var order = (await sale.Owner.Client.CreateOrderAsync(OrderBody(null, sale.ProductId, unit, source: "AtPort"))).GetProperty("order");

        var byManager = await sale.Manager.Client.PostAsJsonAsync($"{Route}/{order.Id()}/cancel", new { reason = "no" });
        var noReason = await sale.Owner.Client.PostAsJsonAsync($"{Route}/{order.Id()}/cancel", new { reason = " " });

        await byManager.ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
        Assert.Equal("Reason", await noReason.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        var seenByManager = await sale.Manager.Client.GetOrderAsync(order.Id());
        Assert.DoesNotContain("Cancel", seenByManager.AllowedActions());
        Assert.Contains("Edit", seenByManager.AllowedActions());
    }

    [Fact]
    public async Task Only_a_delivering_order_is_finished_by_hand_and_a_draft_one_answers_its_status()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit))).GetProperty("order");

        var response = await client.PostAsJsonAsync($"{Route}/{order.Id()}/finish", new { rowVersion = order.RowVersion() });

        await response.ProblemAsync(HttpStatusCode.Conflict, "MSG-GEN-09");
    }

    [Fact]
    // The in-memory provider issues no row versions, so a stale edit is only refused against SQL Server.
    public async Task An_edit_replaces_the_lines_and_keeps_the_line_of_the_same_product_and_unit()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m))).GetProperty("order");
        var lineId = order.GetProperty("lines")[0].Id();
        var edit = OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 30m).WithField("rowVersion", order.RowVersion());

        var updated = (await (await client.PutAsJsonAsync($"{Route}/{order.Id()}", edit)).ReadAsync(HttpStatusCode.OK)).GetProperty("order");

        Assert.Equal((900_000m, lineId), (updated.Money("total"), updated.GetProperty("lines")[0].Id()));
    }

    [Fact]
    public async Task An_edit_takes_a_new_advance_in_the_same_save_within_the_new_total_and_lists_every_advance()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m).WithField("advance", new { amount = 200_000, method = "Cash" }))).GetProperty("order");

        var tooMuch = OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m).WithField("rowVersion", order.RowVersion()).WithField("advance", new { amount = 500_000, method = "Cash" });
        var refused = await client.PutAsJsonAsync($"{Route}/{order.Id()}", tooMuch);
        var fits = OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m).WithField("rowVersion", order.RowVersion()).WithField("advance", new { amount = 300_000, method = "Cash" });
        var saved = await (await client.PutAsJsonAsync($"{Route}/{order.Id()}", fits)).ReadAsync(HttpStatusCode.OK);

        Assert.Equal("Advance amount", await refused.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
        Assert.Equal(2, saved.GetProperty("order").GetProperty("advances").GetArrayLength());
        Assert.Equal(2, (await factory.AdvancesOfOrderAsync(order.Id())).Count);
    }

    [Fact]
    public async Task An_edit_of_a_walk_in_order_takes_no_advance()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var order = (await client.CreateOrderAsync(OrderBody(null, sale.ProductId, unit, source: "AtPort"))).GetProperty("order");

        var edit = OrderBody(null, sale.ProductId, unit, source: "AtPort").WithField("rowVersion", order.RowVersion()).WithField("advance", new { amount = 100_000, method = "Cash" });
        var response = await client.PutAsJsonAsync($"{Route}/{order.Id()}", edit);

        Assert.Equal("Advance", await response.FieldAsync(HttpStatusCode.BadRequest, "MSG-GEN-01"));
    }

    [Fact]
    public async Task The_list_filters_by_code_and_status_and_sums_the_orders_that_are_not_cancelled()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;
        var unit = await client.BaseUnitIdAsync(sale.ProductId);
        var live = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m).WithField("advance", new { amount = 100_000, method = "Cash" }))).GetProperty("order");
        var gone = (await client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit, quantity: 10m))).GetProperty("order");
        await (await client.PostAsJsonAsync($"{Route}/{gone.Id()}/cancel", new { reason = "Duplicate" })).ReadAsync(HttpStatusCode.OK);

        var page = await client.GetFromJsonAsync<JsonElement>($"{Route}?customerId={sale.CustomerId}&pageSize=50");
        var cancelledOnly = await client.GetFromJsonAsync<JsonElement>($"{Route}?customerId={sale.CustomerId}&statusIds={StatusIds.SalesOrder.Cancelled}");
        var byCode = await client.GetFromJsonAsync<JsonElement>($"{Route}/by-code/{live.Text("code")!.ToLowerInvariant()}");

        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal((300_000m, 100_000m), (page.GetProperty("summary").Money("total"), page.GetProperty("summary").Money("customerPaid")));
        Assert.Equal(gone.Id(), Assert.Single(cancelledOnly.GetProperty("items").EnumerateArray()).Id());
        Assert.Equal(live.Id(), byCode.Id());
    }

    [Fact]
    public async Task The_accountant_reads_and_exports_the_orders_and_changes_nothing()
    {
        var sale = await factory.SaleSetupAsync();
        var accountant = await factory.AllZonesAccountantAsync();
        var unit = await sale.Owner.Client.BaseUnitIdAsync(sale.ProductId);
        var order = (await sale.Owner.Client.CreateOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit))).GetProperty("order");

        var read = await accountant.Client.GetOrderAsync(order.Id());
        var export = await accountant.Client.GetAsync($"{Route}/export");
        var create = await accountant.Client.PostOrderAsync(OrderBody(sale.CustomerId, sale.ProductId, unit));

        Assert.DoesNotContain("Edit", read.AllowedActions());
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        await create.ProblemAsync(HttpStatusCode.Forbidden, "MSG-GEN-08");
    }

    [Fact]
    public async Task The_next_code_is_offered_without_being_spent()
    {
        var sale = await factory.SaleSetupAsync();
        var client = sale.Owner.Client;

        var one = await client.GetFromJsonAsync<JsonElement>($"{Route}/next-code");
        var two = await client.GetFromJsonAsync<JsonElement>($"{Route}/next-code");

        Assert.Equal(one.Text("code"), two.Text("code"));
        Assert.Matches("^DH[0-9]{6}$", one.Text("code"));
    }
}
