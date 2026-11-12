using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Invoices;

/// <summary>
/// What a correction or a cancellation does to the customer debt (BR-DEBT-02), worked out before the save so MSG-SALE-17 and MSG-SALE-18 can name it, as pure functions. The debt is
/// never stored: the save itself is the only change, so it moves once (AC-08).
/// </summary>
public static class SalesInvoiceChangeMoney
{
    /// <summary>
    /// The payments the invoice brought that are not Cancelled: what is paid of it, and what moves with it to another profile (BR-SALE-10). A payment still waiting for its
    /// bank transfer changes nothing yet, so it is not paid.
    /// </summary>
    public static decimal PaidOf(IEnumerable<CustomerPayment> payments) =>
        payments.Where(p => p.Kind == CustomerPaymentKind.Payment && p.StatusId is not (StatusIds.CustomerPayment.Cancelled or StatusIds.CustomerPayment.PendingTransfer))
            .Sum(p => p.Amount);

    /// <summary>The same profile: the invoice counts at its new total.</summary>
    public static decimal AfterCorrection(decimal debtBefore, decimal totalBefore, decimal totalAfter) => debtBefore - totalBefore + totalAfter;

    /// <summary>The profile the invoice comes to (another profile, or a walk-in invoice given one): it brings its total and the payments that name it.</summary>
    public static decimal AfterComingIn(decimal debtBefore, decimal total, decimal paid) => debtBefore + total - paid;

    /// <summary>The profile the invoice leaves: its total and its payments go with it.</summary>
    public static decimal AfterLeaving(decimal debtBefore, decimal total, decimal paid) => debtBefore - total + paid;

    /// <summary>SALE-08 on a profile: the invoice no longer counts; a cash payment given back no longer counts either; the other payments still do (POST-2).</summary>
    public static decimal AfterCancellation(decimal debtBefore, decimal total, decimal givenBack) => debtBefore - total + givenBack;

    /// <summary>SALE-08 on a walk-in invoice given a profile: the invoice counts nowhere, its kept payments now count for the profile (SALE-08.2).</summary>
    public static decimal AfterWalkInCancelled(decimal debtBefore, decimal keptPayments) => debtBefore - keptPayments;
}
