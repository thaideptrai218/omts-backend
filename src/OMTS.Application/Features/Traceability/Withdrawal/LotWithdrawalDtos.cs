using OMTS.Application.Features.Harvest;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Traceability.Withdrawal;

/// <summary>TRACE-04 step 2: the reason for the withdrawal, required (BR-TRACE-06), at most 1,000 characters; it stays internal.</summary>
public sealed record WithdrawLotRequest(string? Reason);

/// <summary>
/// TRACE-04 step 1 (SCR-TRACE-01): the traders the withdrawal of a Published lot would notify now, and the question to ask before it. Changes nothing.
/// </summary>
/// <param name="WalkInInvoiceCodes">Current invoices of walk-in customers: nobody to notify, listed for the Cooperative Owner.</param>
/// <param name="Message">MSG-GEN-04, asked before the withdrawal (step 3).</param>
/// <param name="Notice">MSG-TRACE-06 when no current sales invoice sells the lot (TRACE-04.2), else null.</param>
public sealed record LotWithdrawalPreview(
    Guid LotId,
    string LotCode,
    HarvestLotOriginState OriginState,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<WithdrawalTrader> Traders,
    IReadOnlyList<string> WalkInInvoiceCodes,
    string Message,
    string MessageCode,
    string? Notice,
    string? NoticeCode);

/// <summary>TRACE-04 step 7: the withdrawal notice of one trader and whether it was sent, so that the Cooperative Owner calls the others.</summary>
/// <param name="Channel">The channel of the sending: SMS until the Zalo OA templates are approved.</param>
/// <param name="PhoneNumber">The phone the SMS went to; null when the profile had none.</param>
/// <param name="DeliveryStatusId">Sent (accepted by the service) or Not sent (refused, failed, or no phone number); read again while the withdrawal still sends, also Sending.</param>
/// <param name="NotifiedInApp">The notice is also in the list of the trader account and pushed unless the trader turned it off (TRACE-04.1); false without an active trader account.</param>
/// <param name="ToCall">The SMS was not sent: the Cooperative Owner calls this trader (BR-TRACE-07).</param>
public sealed record WithdrawalNotice(
    Guid CustomerId,
    string CustomerName,
    string? PhoneNumber,
    Guid? TraderAccountId,
    IReadOnlyList<string> InvoiceCodes,
    NotificationChannel Channel,
    int DeliveryStatusId,
    string DeliveryStatus,
    bool NotifiedInApp,
    bool ToCall);

/// <summary>The answer of a withdrawal: the lot, now Withdrawn, and the result of each notice (TRACE-04 steps 5-7).</summary>
/// <param name="Notices">One per trader, in the order of the preview; empty when no trader was notified.</param>
/// <param name="Message">MSG-GEN-05.</param>
/// <param name="Notice">MSG-TRACE-06 when no current sales invoice sold the lot, else null.</param>
public sealed record LotWithdrawn(
    HarvestLotView Lot,
    IReadOnlyList<WithdrawalNotice> Notices,
    IReadOnlyList<string> WalkInInvoiceCodes,
    string Message,
    string MessageCode,
    string? Notice,
    string? NoticeCode);

/// <summary>
/// The notices of the latest withdrawal of the lot as saved, with their delivery status now (TRACE-04 step 7 read again, SCR-TRACE-01): a lost answer of the withdrawal does not lose
/// the traders to call. Empty lists and no <paramref name="WithdrawnAt"/> when the lot was never withdrawn.
/// </summary>
/// <param name="WithdrawnAt">The time of the latest withdrawal, also after the lot was published again.</param>
public sealed record LotWithdrawalNotices(
    Guid LotId,
    string LotCode,
    HarvestLotOriginState OriginState,
    DateTimeOffset? WithdrawnAt,
    IReadOnlyList<WithdrawalNotice> Notices,
    IReadOnlyList<string> WalkInInvoiceCodes);
