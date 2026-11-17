using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Sales.Payments;

/// <summary>DEBT-02.2 step 2 and DEBT-02.6 as pure rules: which payment is marked, what a settlement must choose, and the date and time of the money paid back.</summary>
public static class CustomerPaymentSettlementRules
{
    /// <summary>As MSG-GEN-09 names a marked payment (DEBT-02.2 step 3): "… is without a customer profile and cannot be used for this action."</summary>
    public const string NeedsSettlementStatus = "without a customer profile";

    /// <summary>As MSG-GEN-09 names a payment that has nothing to settle.</summary>
    public const string NotMarkedStatus = "already tied to a customer or its invoice";

    /// <summary>As MSG-GEN-09 names a payment whose money paid back stands.</summary>
    public const string PaidBackStatus = "settled by money paid back";

    /// <summary>
    /// A payment Awaiting confirmation with no customer and no saved invoice: a transfer (GFR-08) whose code matches no saved invoice (Stop sale, an unknown code, a transfer short of
    /// its sale). It is settled (DEBT-02.6), never confirmed plainly. A transfer of a saved invoice is Completed when it arrives, and cash at the port always names its invoice.
    /// </summary>
    public static bool NeedsSettlement(CustomerPayment payment) =>
        payment.StatusId == StatusIds.CustomerPayment.AwaitingConfirmation && payment.CustomerId is null && payment.InvoiceId is null && payment.Kind == CustomerPaymentKind.Payment;

    /// <summary>DEBT-02.6 step 2: a customer profile, or Pay money back; exactly one of the two.</summary>
    public static bool ChoosesOneWay(SettleInConfirmations settle) => (settle.CustomerId is null) != (settle.PayBack is null);

    /// <summary>The money paid back cannot be dated before the transfer it pays back (the date of the transfer, at UTC+7).</summary>
    public static bool PaidBackBeforeTransfer(DateOnly? paymentDate, CustomerPayment transfer) => paymentDate is { } date && date < transfer.PaymentDate;

    /// <summary>The time the money was paid back: the time of <paramref name="paidAt"/>, never before the transfer itself (the same day, an earlier time of the day).</summary>
    public static DateTimeOffset PaidBackAt(DateTimeOffset paidAt, CustomerPayment transfer) => paidAt < transfer.PaidAt ? transfer.PaidAt : paidAt;
}
