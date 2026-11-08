using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Harvest;

/// <summary>What the daily close check did to one lot.</summary>
public enum HarvestLotAutoCloseOutcome
{
    /// <summary>Not due, already handled, no longer for sale, or nobody to warn.</summary>
    Nothing,

    Closed,
    Warned,
}

/// <summary>
/// GFR-05, the daily close check at the start of the day, one lot per call so that a conflict costs only that lot. A lot still for sale whose days before automatic close have passed is
/// Closed with that day as the close date, the reason Time limit reached and the remaining kg as the leftover kg, audited with the system as the actor (no account). A lot the next check
/// closes gets MSG-HARVEST-05, and its marker holds the close date it announced, saved with the notifications: a second run, a catch-up or a second app instance sends nothing more. A missed
/// day closes the overdue lots at the next run, with the run day as the close date. A sale, a close by hand or another instance saving the lot meanwhile makes this save fail on the row version;
/// the job skips the lot and the next run checks it again.
/// </summary>
public sealed class HarvestLotAutoCloseService(
    IHarvestLotRepository lots,
    ICooperativeRepository cooperatives,
    IHarvestLotSalesQuery sales,
    PendingSaleLotQuery pendingSales,
    HarvestLotStatusService statuses,
    HarvestLotNotifier notifier,
    IUnitOfWork unitOfWork)
{
    /// <summary>The lots the check of <paramref name="today"/> may close or warn; <see cref="CheckAsync"/> reads each one again.</summary>
    public async Task<IReadOnlyList<Guid>> CandidateIdsAsync(DateOnly today, CancellationToken cancellationToken) =>
        await cooperatives.GetCurrentAsync(cancellationToken) is { } cooperative
            ? await lots.ListForSaleSinceAtMostAsync(HarvestLotAutoCloseRule.LatestSinceCheckedOn(today, cooperative.DaysBeforeAutoClose), cancellationToken)
            : [];

    /// <summary>Closes or warns the lot when it is due on <paramref name="today"/>, with the days set now (BR-HARVEST-06).</summary>
    /// <exception cref="Common.Exceptions.ConcurrencyConflictException">The lot was saved after it was read here; nothing is kept.</exception>
    public async Task<HarvestLotAutoCloseOutcome> CheckAsync(Guid lotId, DateOnly today, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        var cooperative = await cooperatives.GetCurrentAsync(cancellationToken);
        if (lot is not { IsForSale: true } || cooperative is null)
        {
            return HarvestLotAutoCloseOutcome.Nothing;
        }

        var since = HarvestLotAutoCloseRule.ForSaleSince(lot);
        var days = cooperative.DaysBeforeAutoClose;
        if (HarvestLotAutoCloseRule.ClosesAt(since, days, today))
        {
            var figures = await sales.FiguresAsync([lot.Id], cancellationToken);
            // The kg of a sale awaiting its VietQR transfer are not leftover: the transfer invoices them on the closed lot (SALE-05 step 8), so they are never both lost and sold.
            var awaitingKg = (await pendingSales.AwaitingAsync(lot.Id, cancellationToken)).Kg;
            var remainingKg = Math.Max(0m, HarvestLotFigures.Of(lot, figures[lot.Id]).RemainingKg - awaitingKg);
            if (statuses.Close(lot, today, remainingKg, HarvestLotCloseReason.TimeLimitReached) is { } refused)
            {
                throw new InvalidOperationException($"The daily close check could not close lot {lot.LotCode}: {refused.Message.Text}");
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            return HarvestLotAutoCloseOutcome.Closed;
        }

        var closeDate = HarvestLotAutoCloseRule.ClosesOn(since, days);
        if (!HarvestLotAutoCloseRule.WarnsAt(since, days, today) || lot.AutoCloseWarnedFor == closeDate
            || !await notifier.AutoCloseWarningAsync(lot, lot.Raft, closeDate, cancellationToken))
        {
            return HarvestLotAutoCloseOutcome.Nothing;
        }

        lot.AutoCloseWarnedFor = closeDate;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return HarvestLotAutoCloseOutcome.Warned;
    }
}
