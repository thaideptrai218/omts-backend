using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Constants;
using OMTS.Infrastructure.Messaging.Fakes;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;
using static OMTS.Api.IntegrationTests.Sales.PurchaseRequestTestKit;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>SALE-01, BR-SALE-01: one product, about how many kg, the pickup date; what is refused, and who hears of it.</summary>
public class PurchaseRequestPlaceTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task A_request_is_saved_as_awaiting_response_for_the_traders_own_profile_with_msg_gen_05_and_an_audit_row()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(unitPrice: 60_000);
        var trader = await factory.TraderAsync();
        var pickup = Today.AddDays(3);

        var response = await trader.Client.PostRequestAsync(product.Id(), 150.5m, pickup, "  Sáng sớm  ");

        var body = await response.ReadAsync(HttpStatusCode.Created);
        Assert.Equal(("Purchase request saved.", "MSG-GEN-05"), (body.Text("message"), body.Text("messageCode")));
        var request = body.GetProperty("request");
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, request.GetProperty("statusId").GetInt32());
        Assert.Equal("Awaiting response", request.Text("status"));
        Assert.Equal(150.5m, request.GetProperty("requestedKg").GetDecimal());
        Assert.Equal(pickup, request.GetProperty("pickupDate").GetFromJson<DateOnly>());
        Assert.Equal("Sáng sớm", request.Text("note"));
        Assert.True(request.GetProperty("canCancel").GetBoolean());
        Assert.False(request.GetProperty("fulfilled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, request.GetProperty("response").ValueKind);

        var stored = await factory.DbAsync(db => db.PurchaseRequests.FindAsync(request.Id()).AsTask());
        Assert.Equal(trader.Profile.Id, stored!.CustomerId);
        var audit = Assert.Single(await factory.AuditRowsAsync(request.Id()));
        Assert.Equal(("PurchaseRequest.Sent", trader.Account.Id), (audit.Action, audit.ActorAccountId));
    }

    [Fact]
    public async Task The_body_cannot_name_another_profile_or_a_price()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        var trader = await factory.TraderAsync();
        var victim = await factory.TraderAsync();

        var response = await trader.Client.PostAsJsonAsync("/api/me/purchase-requests", new
        {
            productId = product.Id(), requestedKg = 10, pickupDate = Today, customerId = victim.Profile.Id, unitPrice = 1, statusId = StatusIds.PurchaseRequest.Accepted,
        });

        var request = (await response.ReadAsync(HttpStatusCode.Created)).GetProperty("request");
        Assert.Equal(StatusIds.PurchaseRequest.AwaitingResponse, request.GetProperty("statusId").GetInt32());
        Assert.False(request.TryGetProperty("unitPrice", out _));
        Assert.Equal(trader.Profile.Id, (await factory.DbAsync(db => db.PurchaseRequests.FindAsync(request.Id()).AsTask()))!.CustomerId);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(1500.125)]
    [InlineData(999999999999.999)]
    public async Task Any_weight_above_zero_with_three_decimals_is_a_requested_kg(double kg)
    {
        var product = await (await factory.PurchasingOwnerAsync()).CreateProductAsync();
        var trader = await factory.TraderAsync();

        var request = await trader.Client.PlaceAsync(product.Id(), (decimal)kg);

        Assert.Equal((decimal)kg, request.GetProperty("requestedKg").GetDecimal());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0004")]
    [InlineData("1.2345")]
    [InlineData("1000000000000000")]
    [InlineData("null")]
    public async Task Other_weights_are_msg_gen_01(string kg)
    {
        var product = await (await factory.PurchasingOwnerAsync()).CreateProductAsync();
        var trader = await factory.TraderAsync();

        var response = await trader.Client.PostAsync("/api/me/purchase-requests", new StringContent(
            $$"""{ "productId": "{{product.Id()}}", "requestedKg": {{kg}}, "pickupDate": "{{Today.AddDays(1):yyyy-MM-dd}}" }""",
            System.Text.Encoding.UTF8,
            "application/json"));

        Assert.Equal(["Requested kg"], await response.InvalidFieldsAsync());
    }

    [Fact]
    public async Task A_weight_that_cannot_be_read_is_a_400_and_saves_nothing()
    {
        var product = await (await factory.PurchasingOwnerAsync()).CreateProductAsync();
        var trader = await factory.TraderAsync();
        foreach (var kg in new[] { "\"abc\"", "1e400", "[1]", "true" })
        {
            var response = await trader.Client.PostAsync("/api/me/purchase-requests", new StringContent(
                $$"""{ "productId": "{{product.Id()}}", "requestedKg": {{kg}}, "pickupDate": "{{Today.AddDays(1):yyyy-MM-dd}}" }""",
                System.Text.Encoding.UTF8,
                "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await factory.DbAsync(db => Task.FromResult(db.PurchaseRequests.Count(r => r.CustomerId == trader.Profile.Id))));
    }

    [Fact]
    public async Task The_pickup_date_may_be_today_and_not_before_it()
    {
        var product = await (await factory.PurchasingOwnerAsync()).CreateProductAsync();
        var trader = await factory.TraderAsync();

        await trader.Client.PlaceAsync(product.Id(), pickupDate: Today);

        Assert.Equal(["Pickup date"], await (await trader.Client.PostRequestAsync(product.Id(), pickupDate: Today.AddDays(-1))).InvalidFieldsAsync());
        var missing = await trader.Client.PostAsJsonAsync("/api/me/purchase-requests", new { productId = product.Id(), requestedKg = 10 });
        Assert.Equal(["Pickup date"], await missing.InvalidFieldsAsync());
    }

    [Fact]
    public async Task Every_field_to_fix_is_pointed_out()
    {
        var trader = await factory.TraderAsync();

        var response = await trader.Client.PostAsJsonAsync("/api/me/purchase-requests", new { requestedKg = 0, pickupDate = Today.AddDays(-1), note = new string('x', 1001) });

        Assert.Equal(["Product", "Requested kg", "Pickup date", "Note"], await response.InvalidFieldsAsync());
        Assert.Equal(["Product"], await (await trader.Client.PostRequestAsync(Guid.NewGuid())).InvalidFieldsAsync());
    }

    // SALE-01.0.E2: the product was discontinued while the trader was writing the request.
    [Fact]
    public async Task A_discontinued_product_is_msg_gen_09_naming_it_and_a_resumed_one_can_be_requested_again()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync(name: $"Hàu {NewCode()}");
        var trader = await factory.TraderAsync();
        (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).EnsureSuccessStatusCode();

        var refused = await (await trader.Client.PostRequestAsync(product.Id())).ProblemAsync(HttpStatusCode.BadRequest, "MSG-GEN-09");

        Assert.Equal($"Product {product.Text("name")} is Discontinued and cannot be used for this action.", refused.Text("detail"));
        Assert.Equal("Product", refused.Text("field"));
        Assert.Equal(0, await factory.DbAsync(db => Task.FromResult(db.PurchaseRequests.Count(r => r.CustomerId == trader.Profile.Id))));

        (await owner.PostAsync($"/api/products/{product.Id()}/resume", null)).EnsureSuccessStatusCode();
        await trader.Client.PlaceAsync(product.Id());
    }

    [Fact]
    public async Task A_field_error_is_shown_before_the_discontinued_product()
    {
        var owner = await factory.PurchasingOwnerAsync();
        var product = await owner.CreateProductAsync();
        (await owner.PostAsync($"/api/products/{product.Id()}/discontinue", null)).EnsureSuccessStatusCode();
        var trader = await factory.TraderAsync();

        Assert.Equal(["Requested kg"], await (await trader.Client.PostRequestAsync(product.Id(), 0)).InvalidFieldsAsync());
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("​﻿")]
    public async Task A_note_that_shows_nothing_is_no_note(string note)
    {
        var product = await (await factory.PurchasingOwnerAsync()).CreateProductAsync();
        var trader = await factory.TraderAsync();

        var request = await trader.Client.PlaceAsync(product.Id(), note: note);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, request.GetProperty("note").ValueKind);
    }
}
