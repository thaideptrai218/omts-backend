using OMTS.Application.Features.Sales.Payments;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The fields of a price cut once their shapes are right.</summary>
/// <param name="CustomerId">A walk-in invoice with money to give back gets a profile (SALE-07.2); null keeps the current one.</param>
internal sealed record SalesInvoiceCorrectionEntries(string Reason, Guid? CustomerId, decimal PriceCutAmount)
{
    public SalesInvoiceCorrectionKind Kind => SalesInvoiceCorrectionKind.PriceCutAfterClaim;
}

/// <summary>
/// SALE-07.0.E2: the shape of each entry (MSG-GEN-01 names every field to fix). Only the price cut after a claim is entered here: a data entry error is changed with the edit of the invoice
/// (<see cref="SalesInvoiceEditService"/>), so that kind is refused with the way to do it.
/// </summary>
internal static class SalesInvoiceCorrectionEntryReader
{
    public const string UseEditHint = "A data entry error is changed with PUT api/sales-invoices/{id}; this endpoint only enters a price cut after a claim.";

    public static (SalesInvoiceCorrectionEntries? Entries, List<string> Invalid, string? Hint) Read(CorrectSalesInvoiceRequest request)
    {
        var invalid = new List<string>();
        string? hint = null;
        var reason = CustomerPaymentRules.Reason(request.Reason);
        if (reason is null)
        {
            invalid.Add("Reason");
        }

        if (request.Kind is not { } kind || !Enum.IsDefined(kind))
        {
            invalid.Add("Kind");
        }
        else if (kind != SalesInvoiceCorrectionKind.PriceCutAfterClaim)
        {
            invalid.Add("Kind");
            hint = UseEditHint;
        }
        else if (request.Lines is not null || request.NotSoldWholeLotIds is not null || request.Discount is not null || request.OtherCharges is not null)
        {
            invalid.Add("Kind");
            hint = UseEditHint;
        }

        if (request.PriceCutAmount is not > 0m || !InvoiceMoney.IsWholeAmount(request.PriceCutAmount))
        {
            invalid.Add("Price cut amount");
        }

        return invalid.Count > 0 ? (null, invalid, hint) : (new SalesInvoiceCorrectionEntries(reason!, request.CustomerId, request.PriceCutAmount!.Value), invalid, null);
    }
}
