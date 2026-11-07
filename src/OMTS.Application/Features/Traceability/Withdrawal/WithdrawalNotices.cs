using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Features.Notifications;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Application.Features.Traceability.Withdrawal;

/// <summary>One notice queued in the save of a withdrawal: the sending to the trader's phone, and whether the trader account also has it in its list.</summary>
public sealed record QueuedWithdrawalNotice(WithdrawalTrader Trader, Notification Sending, bool NotifiedInApp);

/// <summary>
/// TRACE-04 steps 6 and TRACE-04.1: one MSG-TRACE-05 per trader, listing their invoices (BR-TRACE-07), added to the caller's save. The sending to the phone is kept with its delivery status
/// (BR-SALE-09) and delivered by the caller after the save (<see cref="TraderMessageService.DeliverAsync"/>); a profile without a phone gets a Not sent row at once. A trader with an
/// active account also gets the notice in its list, pushed after the save. Nothing here saves.
/// </summary>
public sealed class WithdrawalNotices(
    ICooperativeRepository cooperatives, INotificationRepository notifications, INotificationDispatcher dispatcher, TraderMessageService traderMessages)
{
    public const string Title = "Origin information withdrawn";

    /// <summary>Error code of the Not sent row of a profile without a phone number.</summary>
    public const string NoPhoneNumber = "NO_PHONE_NUMBER";

    public async Task<IReadOnlyList<QueuedWithdrawalNotice>> QueueAsync(HarvestLot lot, IReadOnlyList<WithdrawalTrader> traders, CancellationToken cancellationToken)
    {
        if (traders.Count == 0)
        {
            return [];
        }

        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken)
            ?? throw new InvalidOperationException("The cooperative has not been created yet (run the bootstrap command).");
        // {cooperative_short_name}: the full name stands in until the Owner saves a short name (FARM-01), as in MSG-SALE-15.
        var cooperativeName = cooperative.ShortName ?? cooperative.FullName;
        var accountIds = traders.Where(t => t.TraderAccountId is not null).Select(t => t.TraderAccountId!.Value).Distinct().ToList();
        // Locked accounts get nothing in a list (as the dispatcher does): one query for every trader.
        HashSet<Guid> active = accountIds.Count == 0 ? [] : (await notifications.GetPreferencesAsync(accountIds, cancellationToken)).Keys.ToHashSet();

        var queued = new List<QueuedWithdrawalNotice>(traders.Count);
        foreach (var trader in traders)
        {
            var message = UserMessages.ProvenanceWithdrawnNotice(cooperativeName, lot.LotCode, trader.InvoiceCodes, cooperative.PhoneNumber);
            var content = new NotificationContent(NotificationTypes.ProvenanceWithdrawn, Title, message, new RelatedRecord(RelatedRecordTypes.HarvestLot, lot.Id, lot.LotCode));
            var inApp = trader.TraderAccountId is { } accountId && active.Contains(accountId);
            if (inApp)
            {
                // The list row opens nothing: the lot is not a record of the trader, who is asked to call.
                await dispatcher.NotifyAsync([trader.TraderAccountId!.Value], content with { Record = null }, cancellationToken);
            }

            var sending = string.IsNullOrWhiteSpace(trader.PhoneNumber)
                ? traderMessages.NotSent(trader.TraderAccountId, content, NoPhoneNumber)
                : traderMessages.Queue(trader.TraderAccountId, trader.PhoneNumber, content);
            queued.Add(new QueuedWithdrawalNotice(trader, sending, inApp));
        }

        return queued;
    }
}
