using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OMTS.Api.IntegrationTests.Infrastructure;
using OMTS.Api.IntegrationTests.Purchasing;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Api.IntegrationTests.Sales;

/// <summary>Order calls: the body as the clients send it, and what an order leaves behind.</summary>
public static class SalesOrderTestKit
{
    public const string Route = "/api/sales-orders";

    /// <summary>The kg unit of the product, the unit its list price is for.</summary>
    public static async Task<Guid> BaseUnitIdAsync(this HttpClient client, Guid productId)
    {
        var product = await client.GetFromJsonAsync<JsonElement>($"/api/products/{productId}");
        return product.GetProperty("units").EnumerateArray().First(u => u.GetProperty("isBase").GetBoolean()).GetProperty("unitId").GetGuid();
    }

    /// <summary>An order body for one line in the unit given (an order has no zone); the buyer is a walk-in when <paramref name="customerId"/> is null.</summary>
    public static Dictionary<string, object?> OrderBody(
        Guid? customerId, Guid productId, Guid unitId, decimal quantity = 20m, string source = "Phone", decimal? unitPrice = null) => new()
    {
        ["customerId"] = customerId,
        ["walkInCustomer"] = customerId is null,
        ["source"] = source,
        ["discount"] = 0,
        ["discountDecidedByOwner"] = false,
        ["lines"] = new object[] { new { productId, unitId, quantity, unitPrice } },
    };

    public static Dictionary<string, object?> WithField(this Dictionary<string, object?> body, string field, object? value)
    {
        body[field] = value;
        return body;
    }

    public static Task<HttpResponseMessage> PostOrderAsync(this HttpClient client, object body) => client.PostAsJsonAsync(Route, body);

    /// <summary>Saves the order and returns the whole answer (<c>order</c>, <c>message</c>, <c>advanceQr</c>).</summary>
    public static async Task<JsonElement> CreateOrderAsync(this HttpClient client, object body) => await (await client.PostOrderAsync(body)).ReadAsync(HttpStatusCode.Created);

    public static async Task<JsonElement> GetOrderAsync(this HttpClient client, Guid orderId) => await (await client.GetAsync($"{Route}/{orderId}")).ReadAsync(HttpStatusCode.OK);

    public static Task<List<CustomerPayment>> AdvancesOfOrderAsync(this AuthApiFactory api, Guid orderId) =>
        api.QueryAsync(db => db.CustomerPayments.AsNoTracking().Where(p => p.SalesOrderId == orderId).OrderBy(p => p.PaidAt).ToListAsync());

    public static Task<int> OrderCountAsync(this AuthApiFactory api, string code) =>
        api.QueryAsync(db => db.SalesOrders.AsNoTracking().CountAsync(o => o.Code == code));

    public static IEnumerable<string> AllowedActions(this JsonElement order) => order.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()!);
}
