using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Purchasing.Issues;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>
/// A nursery batch and its stock issue (Xuất dùng) are one fact (decision of 12/11/2026): the pieces must be on hand in the seed lot (MSG-INPUT-01 with the balance, 409; no negative stock unless
/// the cooperative allows it), and the same save writes, changes or cancels the linked issue. The issue is for the zone: it names no raft. Nothing is saved here: the callers save the batch and the
/// issue together, and the seeding from the batch's cycles posts no issue of its own.
/// </summary>
public sealed class NurseryStockSync(FarmingStockIssuer issuer, IInputIssueRepository issues, SeedingStockSync seedingStock)
{
    /// <summary>
    /// MSG-INPUT-01 when the pieces are above what is on hand in the lot, also when the cooperative allows a negative stock (owner decision of 12/11/2026: no negative stock for a nursery);
    /// <paramref name="own"/> is what the batch already holds (an edit may keep using it).
    /// </summary>
    internal Task<AppError?> CheckOnHandAsync(PurchaseReceiptLine lot, decimal quantity, decimal own, CancellationToken cancellationToken) =>
        seedingStock.CheckOnHandAsync(lot, quantity, own, cancellationToken, enforceAlways: true);

    /// <summary>A new batch: writes the issue and links it, then answers MSG-INPUT-01 when the ledger would go below zero.</summary>
    internal async Task<AppError?> PostAsync(NurseryBatch batch, PurchaseReceiptLine lot, CancellationToken cancellationToken)
    {
        var issue = await issuer.PostAsync(InputIssueSource.Nursery, batch.Id, Place(batch), null, batch.StartDate, Lines(lot, batch.QuantityIn), InputIssueTypeIds.Nursery, cancellationToken);
        (batch.IssueId, batch.Issue) = (issue.Id, issue);
        return await issuer.CheckNotNegativeAsync(cancellationToken);
    }

    /// <summary>An edited batch, called after its fields changed: the issue follows the new date and pieces.</summary>
    internal async Task<AppError?> ReplaceAsync(NurseryBatch batch, PurchaseReceiptLine lot, CancellationToken cancellationToken)
    {
        if (batch.IssueId is { } issueId && await issues.GetForEditAsync(issueId, cancellationToken) is { } issue)
        {
            await issuer.ReplaceAsync(issue, Place(batch), null, batch.StartDate, Lines(lot, batch.QuantityIn), cancellationToken);
        }

        return await issuer.CheckNotNegativeAsync(cancellationToken);
    }

    /// <summary>A cancelled batch: its pieces return to the lot and the issue is Cancelled.</summary>
    internal async Task CancelAsync(NurseryBatch batch, string reason, CancellationToken cancellationToken)
    {
        if (batch.IssueId is { } issueId && await issues.GetForEditAsync(issueId, cancellationToken) is { } issue)
        {
            await issuer.CancelAsync(issue, batch.ZoneId, reason, cancellationToken);
        }
    }

    private static StockPlace Place(NurseryBatch batch) => new(batch.ZoneId, null, batch.Zone.Code);

    private static List<FarmingIssueLine> Lines(PurchaseReceiptLine lot, decimal quantity) => [new FarmingIssueLine(lot.Item!, lot.Id, quantity)];
}
