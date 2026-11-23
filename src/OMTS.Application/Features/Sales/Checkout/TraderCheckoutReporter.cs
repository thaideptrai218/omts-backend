using Microsoft.Extensions.Logging;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces.Payments;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Sales.Checkout;

/// <summary>
/// Tells Zalo that the order of a checkout was paid (Checkout SDK <c>custom-callback-payment</c>, <c>resultCode</c> 1), once the payment arrived in full. A failure is kept
/// on the checkout and sent again by the next poll of the Mini App, the next notification or the retry job, at most every <see cref="TraderCheckoutRules.ZaloReportGap"/>
/// and <see cref="TraderCheckoutRules.MaxZaloReports"/> times. Zalo's record only shows the payment to the trader: OMTS has recorded the money before this runs.
/// </summary>
public sealed class TraderCheckoutReporter(
    ITraderCheckoutRepository checkouts,
    IZaloCheckout zalo,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<TraderCheckoutReporter> logger)
{
    /// <summary>The checkout is paid in full, has a Zalo order and Zalo has not accepted a report yet; the gap since the last report is not checked here.</summary>
    public static bool NeedsReport(TraderCheckout checkout) =>
        checkout.ZaloOrderId is not null && checkout.ZaloReportedAt is null && checkout.ZaloReportAttempts < TraderCheckoutRules.MaxZaloReports
        && TraderCheckoutRules.PaidInFull(checkout);

    /// <summary>Reports a tracked checkout when it needs it and the last report is older than the gap (<paramref name="ignoreGap"/>: just after the money arrived).</summary>
    public async Task ReportAsync(TraderCheckout checkout, bool ignoreGap, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!NeedsReport(checkout) || !zalo.IsConfigured
            || (!ignoreGap && checkout.ZaloLastAttemptAt is { } last && now - last < TraderCheckoutRules.ZaloReportGap))
        {
            return;
        }

        var report = await zalo.ReportPaidAsync(checkout.ZaloOrderId!, cancellationToken);
        checkout.ZaloReportAttempts++;
        checkout.ZaloLastAttemptAt = now;
        if (report.Accepted)
        {
            checkout.ZaloReportedAt = now;
            checkout.ZaloLastError = null;
        }
        else
        {
            checkout.ZaloLastError = TraderCheckoutRules.Truncated(report.Error, TraderCheckoutRules.MaxZaloErrorLength);
            logger.LogWarning(
                "Zalo did not accept the payment report of checkout {CheckoutId} (attempt {Attempt}): {Error}", checkout.Id, checkout.ZaloReportAttempts, checkout.ZaloLastError);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Another poll or the job reported it at the same moment: theirs is kept, and a report still missing is sent again later.
            unitOfWork.DiscardChanges();
        }
    }

    /// <summary>The retry job: the checkouts still to report, each read and saved on its own. Returns how many Zalo accepted.</summary>
    public async Task<int> RetryAsync(int take, CancellationToken cancellationToken)
    {
        if (!zalo.IsConfigured)
        {
            return 0;
        }

        var ids = await checkouts.ListToReportAsync(timeProvider.GetUtcNow() - TraderCheckoutRules.ZaloReportGap, TraderCheckoutRules.MaxZaloReports, take, cancellationToken);
        var accepted = 0;
        foreach (var id in ids)
        {
            unitOfWork.DiscardChanges();
            if (await checkouts.GetForEditAsync(id, cancellationToken) is not { } checkout)
            {
                continue;
            }

            await ReportAsync(checkout, ignoreGap: false, cancellationToken);
            if (checkout.ZaloReportedAt is not null)
            {
                accepted++;
            }
        }

        return accepted;
    }
}
