using OMTS.Application.Features.Sales.Payments;

namespace OMTS.Application.Features.Sales.Checkout.Dtos;

/// <summary>What was done with a payOS notification whose signature was right (the webhook answers 200 with it, so payOS does not send it again).</summary>
/// <param name="Outcome">How the transfer was recorded; null when nothing was recorded.</param>
/// <param name="Ignored">Why nothing was recorded: <c>not-successful</c> (payOS reported no payment), <c>unknown-order</c> (no checkout has the order code, as the test
/// notification payOS sends when the webhook is registered), <c>no-account</c> (the cooperative has no receiving bank account any more).</param>
public sealed record PayOsNoticeResult(BankTransferOutcome? Outcome, string? PaymentCode, string? Ignored)
{
    public static PayOsNoticeResult Skipped(string reason) => new(null, null, reason);
}
