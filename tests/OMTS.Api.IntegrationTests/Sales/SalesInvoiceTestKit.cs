using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Cultivation;
using OMTS.Api.IntegrationTests.Farming;
using OMTS.Api.IntegrationTests.Harvest;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>A zone with a raft in an Open cycle and two lots for sale, a product at 30,000 VND/kg (AC-06), a customer profile and the Zone Manager of the zone.</summary>
public sealed record SaleSetup(Actor Owner, Actor Manager, Guid ZoneId, Guid RaftId, Guid LotId, Guid OtherLotId, Guid ProductId, Guid CustomerId);

/// <summary>SALE-05 calls: the body as the clients send it, and what a sale leaves behind.</summary>
public static class SalesInvoiceTestKit
{
    public const decimal ListPrice = 30_000m;

    public static async Task<SaleSetup> SaleSetupAsync(this AuthApiFactory api)
    {
        var raft = await api.SeededRaftAsync();
        var owner = raft.Owner;
        var lot = await owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(1));
        var other = await owner.Client.HarvestLotAsync(raft.RaftId, HarvestFields.OfLanes(2));
        var product = await owner.Client.CreateProductAsync(unitPrice: ListPrice);
        var customer = await owner.Client.CreateCustomerAsync();
        var manager = await api.ManagerAsync(raft.ZoneId);
        return new SaleSetup(owner, manager, raft.ZoneId, raft.RaftId, lot.Id(), other.Id(), product.Id(), customer.Id());
    }

    public static object Line(Guid productId, Guid lotId, decimal? kg, decimal? unitPrice = null, bool priceDecidedByOwner = false) =>
        new { productId, harvestLotId = lotId, kg, unitPrice, priceDecidedByOwner };

    public static object Cash(decimal amount, string rest = "None", bool? subtractExtraFromDebt = null, bool payLaterOwnerAgreed = false) =>
        new { cashAmount = amount, rest, subtractExtraFromDebt, payLaterOwnerAgreed };

    /// <summary>A body for a customer, or a walk-in customer when <paramref name="customerId"/> is null.</summary>
    public static Dictionary<string, object?> Invoice(Guid? customerId, object[] lines, object payment, params Guid[] notSoldWholeLotIds) => new()
    {
        ["customerId"] = customerId,
        ["walkInCustomer"] = customerId is null,
        ["lines"] = lines,
        ["payment"] = payment,
        ["notSoldWholeLotIds"] = notSoldWholeLotIds,
    };

    public static Dictionary<string, object?> With(this Dictionary<string, object?> body, string field, object? value)
    {
        body[field] = value;
        return body;
    }

    public static Task<HttpResponseMessage> PostInvoiceAsync(this HttpClient client, object body) => client.PostAsJsonAsync("/api/sales-invoices", body);

    /// <summary>Saves the invoice and returns the whole answer.</summary>
    public static async Task<JsonElement> SellAsync(this HttpClient client, object body) => await (await client.PostInvoiceAsync(body)).ReadAsync(HttpStatusCode.Created);

    /// <summary>A 409 question (MSG-SALE-10/11/12): its code and the flag that answers it.</summary>
    public static async Task<(string Code, string Field, JsonElement Body)> QuestionAsync(this HttpResponseMessage response)
    {
        var body = await response.ReadAsync(HttpStatusCode.Conflict);
        return (body.GetProperty("code").GetString()!, body.GetProperty("confirmationField").GetString()!, body);
    }

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static Task<decimal> CustomerDebtAsync(this HttpClient client, Guid customerId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/customers/{customerId}").ContinueWith(t => t.Result.GetProperty("currentDebt").GetDecimal());

    public static Task<List<CustomerPayment>> PaymentsOfAsync(this AuthApiFactory api, Guid invoiceId) =>
        api.QueryAsync(db => db.CustomerPayments.AsNoTracking().Where(p => p.InvoiceId == invoiceId).ToListAsync());
}
