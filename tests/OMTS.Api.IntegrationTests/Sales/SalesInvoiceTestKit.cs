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
        new Dictionary<string, object?> { ["productId"] = productId, ["harvestLotId"] = lotId, ["kg"] = kg, ["unitPrice"] = unitPrice, ["priceDecidedByOwner"] = priceDecidedByOwner };

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

    /// <summary>
    /// Saves the invoice. Every invoice is made from an order, so a body that names none gets one first, saved by the same caller at the port: one order line per invoice line (the product in kg at its
    /// list price, the kg as the quantity), the customer and the zone of the invoice; the invoice lines then name their order lines. A refusal of the order (a zone the caller may not sell in) is
    /// the answer, as the invoice would have been refused for the same reason.
    /// </summary>
    public static async Task<HttpResponseMessage> PostInvoiceAsync(this HttpClient client, object body)
    {
        if (body is Dictionary<string, object?> invoice && !invoice.ContainsKey("salesOrderId") && invoice.GetValueOrDefault("lines") is System.Collections.IEnumerable lines)
        {
            var refused = await client.AttachOrderAsync(invoice, lines.Cast<object?>().ToList());
            if (refused is not null)
            {
                return refused;
            }
        }

        return await client.PostAsJsonAsync("/api/sales-invoices", body);
    }

    /// <summary>The order an invoice is made from, saved by <paramref name="client"/> at the port: one line per product in kg at its list price. Returns the order id and the id of each line.</summary>
    public static async Task<(Guid OrderId, List<Guid> LineIds)> OrderOfAsync(this HttpClient client, Guid? customerId, params (Guid ProductId, decimal Quantity)[] lines)
    {
        var orderLines = new List<object>();
        foreach (var (productId, quantity) in lines)
        {
            orderLines.Add(new { productId, unitId = await client.BaseUnitIdAsync(productId), quantity });
        }

        var body = new Dictionary<string, object?>
        {
            ["customerId"] = customerId, ["walkInCustomer"] = customerId is null, ["source"] = "AtPort", ["discount"] = 0, ["discountDecidedByOwner"] = false, ["lines"] = orderLines,
        };
        var order = (await (await client.PostAsJsonAsync("/api/sales-orders", body)).ReadAsync(HttpStatusCode.Created)).GetProperty("order");
        return (order.GetProperty("id").GetGuid(), order.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList());
    }

    private static async Task<HttpResponseMessage?> AttachOrderAsync(this HttpClient client, Dictionary<string, object?> invoice, List<object?> lines)
    {
        var parts = lines.Select(l => l is null ? default : JsonSerializer.SerializeToElement(l)).ToList();
        string? Text(JsonElement line, string name) => line.ValueKind == JsonValueKind.Object && line.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var first = parts.FirstOrDefault(l => Text(l, "harvestLotId") is not null);
        var zoneId = invoice.GetValueOrDefault("zoneId") as Guid?;
        if (zoneId is null && first.ValueKind == JsonValueKind.Object)
        {
            var lot = await client.GetAsync($"/api/harvest-lots/{Text(first, "harvestLotId")}");
            zoneId = lot.IsSuccessStatusCode ? (await lot.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("zoneId").GetGuid() : null;
        }

        if (zoneId is null)
        {
            // A lot that does not exist: the order goes in a zone the caller can sell in, so the invoice is what is refused for the lot.
            var forSale = await client.GetAsync("/api/harvest-lots/for-sale");
            zoneId = forSale.IsSuccessStatusCode ? (await forSale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lots").EnumerateArray().Select(l => l.GetProperty("zoneId").GetGuid()).Cast<Guid?>().FirstOrDefault() : null;
        }

        if (zoneId is null)
        {
            // No lot the caller can read, so no zone to order in: the invoice names an order that does not exist and the shape of the request is what answers.
            invoice["salesOrderId"] = Guid.NewGuid();
            return null;
        }

        var customerId = invoice.GetValueOrDefault("customerId") as Guid?;
        var orderLines = new List<object>();
        var units = new Dictionary<string, Guid>();
        foreach (var line in parts)
        {
            if (Text(line, "productId") is not { } productId)
            {
                orderLines.Add(new { });
                continue;
            }

            if (!units.TryGetValue(productId, out var unitId))
            {
                // A product that does not exist has no unit: the order is refused for the product, which is what the invoice is refused for too.
                var read = await client.GetAsync($"/api/products/{productId}");
                units[productId] = unitId = read.IsSuccessStatusCode
                    ? (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("units").EnumerateArray().First(u => u.GetProperty("isBase").GetBoolean()).GetProperty("unitId").GetGuid()
                    : Guid.NewGuid();
            }

            var kg = line.TryGetProperty("kg", out var k) && k.ValueKind == JsonValueKind.Number && k.GetDecimal() > 0m && decimal.Round(k.GetDecimal(), 3) == k.GetDecimal() ? k.GetDecimal() : 1m;
            orderLines.Add(new { productId = Guid.Parse(productId), unitId, quantity = kg });
        }

        var order = new Dictionary<string, object?>
        {
            ["customerId"] = customerId, ["walkInCustomer"] = customerId is null, ["source"] = "AtPort", ["discount"] = 0, ["discountDecidedByOwner"] = false, ["lines"] = orderLines,
        };
        var created = await client.PostAsJsonAsync("/api/sales-orders", order);
        if (!created.IsSuccessStatusCode)
        {
            return created;
        }

        var saved = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("order");
        invoice["salesOrderId"] = saved.GetProperty("id").GetGuid();
        var orderLineIds = saved.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] is Dictionary<string, object?> dictionary)
            {
                dictionary["salesOrderLineId"] = orderLineIds[i];
            }
        }

        return null;
    }

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
