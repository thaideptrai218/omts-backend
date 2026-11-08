using OMTS.Application.Features.Sales.Debts;
using OMTS.Domain.Constants;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// Whether an invoice is paid (BR-SALE-08: worked out, never stored). With a customer profile: what <see cref="CustomerDebtAllocator"/> leaves unpaid of it, oldest invoices first
/// (BR-DEBT-05), the figure of DEBT-01, DEBT-03 and MSG-SALE-15. A walk-in invoice counts in no debt, so only its own payments cover it. A Cancelled invoice has nothing to pay.
/// </summary>
/// <param name="IsPaid">Null for a Cancelled invoice.</param>
/// <param name="Covered">The part of the total the payments cover.</param>
/// <param name="Unpaid">The part still to pay.</param>
public sealed record SalesInvoicePaidState(bool? IsPaid, decimal Covered, decimal Unpaid)
{
    /// <param name="ownPayments">The payments of the invoice that are not cancelled: read only for a walk-in invoice.</param>
    /// <param name="allocation">The allocation of the profile of the invoice: read only with a profile.</param>
    public static SalesInvoicePaidState Of(Guid invoiceId, int statusId, decimal total, Guid? customerId, decimal ownPayments, CustomerDebtAllocation? allocation)
    {
        if (statusId == StatusIds.SalesInvoice.Cancelled)
        {
            return new SalesInvoicePaidState(null, 0m, 0m);
        }

        var unpaid = customerId is null ? Math.Max(0m, total - ownPayments) : (allocation ?? CustomerDebtAllocation.None).UnpaidOf(invoiceId);
        return new SalesInvoicePaidState(unpaid == 0m, total - unpaid, unpaid);
    }
}
