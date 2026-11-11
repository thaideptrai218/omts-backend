using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Purchasing.Issues;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.Cultivation.Seeding;

/// <summary>
/// A seeding session and its stock issue (Xuất dùng) are one fact (PM decision 11/11/2026, replacing GBR-04 "no comparison with what is left"): the quantity must be on hand in the seed lot
/// (MSG-INPUT-01 with the balance, 409), and the same save writes, changes or cancels the linked issue. A session recorded before this has no issue (legacy): its seed is counted from the
/// session itself and it stays so (nothing is invented in the ledger), so it is never counted twice. Nothing is saved here: the callers save the session and the issue together.
/// </summary>
public sealed class SeedingStockSync(
    FarmingStockIssuer issuer, IInputIssueRepository issues, ISeedLotRepository seedLots, ISeedLotUsageQuery usage)
{
    /// <summary>A new session: checks the lot, writes the issue and links it.</summary>
    internal async Task<AppError?> PostAsync(SeedingRecord record, SeedingFields fields, Raft raft, CancellationToken cancellationToken)
    {
        if (await CheckOnHandAsync(fields.Lot, fields.Quantity, 0m, cancellationToken) is { } refused)
        {
            return refused;
        }

        var issue = await issuer.PostAsync(InputIssueSource.Seeding, record.Id, raft, record.CycleId, fields.Date, Lines(fields), InputIssueTypeIds.SeedRelease, cancellationToken);
        (record.IssueId, record.Issue) = (issue.Id, issue);
        return await issuer.CheckNotNegativeAsync(cancellationToken);
    }

    /// <summary>A corrected session, called before its row changes: the issue follows the new date, lot and quantity.</summary>
    internal async Task<AppError?> ReplaceAsync(SeedingRecord record, SeedingFields fields, Raft raft, CancellationToken cancellationToken)
    {
        // What the session holds today counts as consumed, so a correction may keep using it.
        var own = record.SeedLotLineId == fields.Lot.Id ? record.Quantity : 0m;
        if (await CheckOnHandAsync(fields.Lot, fields.Quantity, own, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (record.IssueId is { } issueId && await issues.GetForEditAsync(issueId, cancellationToken) is { } issue)
        {
            await issuer.ReplaceAsync(issue, raft, record.CycleId, fields.Date, Lines(fields), cancellationToken);
        }


        // A legacy session (no issue) stays legacy: it is counted from the session itself, so changing it needs no ledger row.
        return await issuer.CheckNotNegativeAsync(cancellationToken);
    }

    /// <summary>A removed session: its quantity returns to the lot and the issue is Cancelled. A legacy session has no issue and nothing to return.</summary>
    internal async Task RemoveAsync(SeedingRecord record, Raft raft, string reason, CancellationToken cancellationToken)
    {
        if (record.IssueId is { } issueId && await issues.GetForEditAsync(issueId, cancellationToken) is { } issue)
        {
            await issuer.CancelAsync(issue, raft, reason, cancellationToken);
        }
    }

    private static List<FarmingIssueLine> Lines(SeedingFields fields) => [new FarmingIssueLine(fields.Lot.Item!, fields.Lot.Id, fields.Quantity)];

    /// <summary>MSG-INPUT-01 when the quantity is above what is on hand in the lot (received, less returns and everything consumed), unless the cooperative allows a negative stock.</summary>
    private async Task<AppError?> CheckOnHandAsync(PurchaseReceiptLine lot, decimal quantity, decimal own, CancellationToken cancellationToken)
    {
        if (await issuer.NegativeAllowedAsync(cancellationToken))
        {
            return null;
        }

        var consumed = (await usage.SeededAsync([lot.Id], cancellationToken)).GetValueOrDefault(lot.Id);
        var returned = await seedLots.ReturnedAsync(lot.Id, cancellationToken);
        var onHand = (lot.Quantity ?? 0m) - returned - consumed + own;
        if (quantity <= onHand)
        {
            return null;
        }

        var item = lot.Item!;
        return AppError.Conflict(
            UserMessages.InputQuantityBelowZero(item.Name, SeedingValues.LotCode(lot), onHand - quantity, item.Unit),
            new Dictionary<string, object?>
            {
                ["field"] = "Quantity", ["itemId"] = item.Id, ["seedLotLineId"] = lot.Id, ["onHand"] = onHand, ["balanceAfter"] = onHand - quantity,
            });
    }
}
