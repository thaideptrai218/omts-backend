using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The fields of SALE-07 once their shapes are right. <see cref="Lines"/> is empty for a price cut, whose <see cref="PriceCutAmount"/> is above 0.</summary>
internal sealed record SalesInvoiceCorrectionEntries(
    SalesInvoiceCorrectionKind Kind, string Reason, Guid? CustomerId, IReadOnlyList<SalesInvoiceLineEntries> Lines, IReadOnlySet<Guid> NotSoldWholeLotIds,
    decimal? Discount, decimal? OtherCharges, decimal PriceCutAmount)
{
    public bool IsPriceCut => Kind == SalesInvoiceCorrectionKind.PriceCutAfterClaim;
}

/// <summary>SALE-07.0.E2: the shape of each entry (MSG-GEN-01 names every field to fix); a kind never carries the fields of the other kind (SALE-07.1).</summary>
internal static class SalesInvoiceCorrectionEntryReader
{
    public static (SalesInvoiceCorrectionEntries? Entries, List<string> Invalid) Read(CorrectSalesInvoiceRequest request)
    {
        var invalid = new List<string>();
        var reason = CustomerPaymentRules.Reason(request.Reason);
        if (reason is null)
        {
            invalid.Add("Reason");
        }

        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            invalid.Add("Kind");
            return (null, invalid);
        }

        var lines = new List<SalesInvoiceLineEntries>();
        var notSoldWhole = (request.NotSoldWholeLotIds ?? []).ToHashSet();
        if (kind == SalesInvoiceCorrectionKind.PriceCutAfterClaim)
        {
            if (request.Lines is not null || request.NotSoldWholeLotIds is not null || request.Discount is not null || request.OtherCharges is not null)
            {
                invalid.Add("Kind");
            }

            if (request.PriceCutAmount is not > 0m || !InvoiceMoney.IsWholeAmount(request.PriceCutAmount))
            {
                invalid.Add("Price cut amount");
            }
        }
        else
        {
            if (request.PriceCutAmount is not null)
            {
                invalid.Add("Kind");
            }

            lines = SalesInvoiceEntryReader.ReadLines(request.Lines, invalid);
            if (!notSoldWhole.IsSubsetOf(lines.Select(l => l.LotId)))
            {
                invalid.Add("Sold the whole lot");
            }

            if (request.Discount is not null && !InvoiceMoney.IsWholeAmount(request.Discount))
            {
                invalid.Add("Discount");
            }

            if (request.OtherCharges is not null && !InvoiceMoney.IsWholeAmount(request.OtherCharges))
            {
                invalid.Add("Other charges");
            }
        }

        return invalid.Count > 0
            ? (null, invalid)
            : (new SalesInvoiceCorrectionEntries(kind, reason!, request.CustomerId, lines, notSoldWhole, request.Discount, request.OtherCharges, request.PriceCutAmount ?? 0m), invalid);
    }
}
