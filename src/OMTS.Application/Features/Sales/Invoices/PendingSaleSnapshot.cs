using System.Text.Json;
using System.Text.Json.Serialization;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>One line as it was priced when the VietQR code was shown.</summary>
internal sealed record PendingSaleLine(int LineNumber, decimal UnitPrice, decimal ListPrice, SalesPriceSource PriceSource, decimal Amount);

/// <summary>
/// What <see cref="PendingSale.SaleJson"/> holds: the request as the Zone Manager confirmed it (every question answered) and the sale as it was decided then. The bank notification
/// saves the invoice from it at these prices and amounts, so the invoice is for the amount of the VietQR code even if a list price changed meanwhile; a change of payment method
/// decides the request again, as a new save at the port (SALE-05.0.E4 step 2).
/// </summary>
/// <param name="ByOwner">The Cooperative Owner made the sale: no MSG-SALE-10 was asked, and Pay later needs no other agreement.</param>
internal sealed record PendingSaleSnapshot(
    CreateSalesInvoiceRequest Request, bool ByOwner, IReadOnlyList<PendingSaleLine> Lines, decimal Subtotal, decimal Total, decimal CashRecorded)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static PendingSaleSnapshot Of(CreateSalesInvoiceRequest request, SalesInvoiceFacts facts, SalesInvoiceDecision decision) => new(
        request with { DeduplicationKey = null },
        facts.ByOwner,
        decision.Lines.Select(l => new PendingSaleLine(l.Entry.LineNumber, l.UnitPrice, l.ListPrice, l.PriceSource, l.Amount)).ToList(),
        decision.Subtotal,
        decision.Total,
        decision.Cash.Recorded);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PendingSaleSnapshot Read(PendingSale sale) =>
        JsonSerializer.Deserialize<PendingSaleSnapshot>(sale.SaleJson, Json) ?? throw new InvalidOperationException($"Pending sale {sale.Code} holds no sale.");
}
