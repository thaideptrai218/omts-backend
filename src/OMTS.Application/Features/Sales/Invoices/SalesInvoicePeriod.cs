using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>The period of SALE-09: the invoices of today by default (step 1), on their business date (BR-SALE-08), at most five years (<see cref="PurchasePeriod.Check"/>).</summary>
public static class SalesInvoicePeriod
{
    /// <summary>No start: the day of the end (today when neither is given). No end: today, so a start in the future answers MSG-GEN-01.</summary>
    public static Result<(DateOnly From, DateOnly To)> Resolve(DateOnly? requestedFrom, DateOnly? requestedTo, DateOnly today)
    {
        var to = requestedTo ?? today;
        return PurchasePeriod.Check(requestedFrom ?? to, to);
    }
}
