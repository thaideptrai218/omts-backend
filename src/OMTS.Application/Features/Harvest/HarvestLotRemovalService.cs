using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Application.Features.Harvest.Plans;
using OMTS.Application.Features.Sales.Invoices;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-01.1 step 3: removes a lot with a reason, while its cycle is Open and it is on no sales invoice (BR-HARVEST-05): a Zone Manager what they recorded, the Cooperative Owner any lot. It stays only
/// in the audit log, its lanes are worked out from the other lots, and its code is not given again. A sale in the same moment marks the lot changed, so the row version makes one of them fail.
/// </summary>
public sealed class HarvestLotRemovalService(
    IHarvestLotRepository lots,
    IHarvestLotSalesQuery sales,
    IProductDisposalRepository disposals,
    HarvestLotProductLineService productLines,
    HarvestPlanProgress planProgress,
    PendingSaleLotQuery pendingSales,
    HarvestLotEditReader edits,
    RowVersionGuard rowVersions,
    UploadedFileSaver fileSaver,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    /// <summary>As MSG-GEN-09 names a lot held by a sale awaiting its transfer.</summary>
    public const string AwaitingTransferStatus = "on a sale awaiting its transfer";

    public async Task<Result<bool>> RemoveAsync(Guid lotId, RemoveHarvestLotRequest request, CancellationToken cancellationToken)
    {
        var lot = await lots.GetForEditAsync(lotId, cancellationToken);
        if (lot is null)
        {
            return AppError.NotFound;
        }

        if (await edits.RefusalAsync(lot, cancellationToken) is { } refused)
        {
            return refused;
        }

        if (await sales.IsOnAnyInvoiceAsync(lot.Id, cancellationToken))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", "on a sales invoice"));
        }

        // Oysters of the lot written off on a disposal (a draft too) stay on the books of that document.
        if (await disposals.IsLotOnDisposalAsync(lot.Id, cancellationToken))
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", "on a product disposal"));
        }

        // A sale awaiting its VietQR transfer will invoice the lot when the money arrives (SALE-05 step 8), so it is as good as on an invoice. A sale created at this moment marks
        // the lot changed, so the row version makes one of the two fail.
        if ((await pendingSales.AwaitingAsync(lot.Id, cancellationToken)).Sales > 0)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Harvest lot {lot.LotCode}", AwaitingTransferStatus));
        }

        var check = new FieldCheck();
        var reason = check.Required(request.Reason, HarvestLotFieldReader.ReasonField, HarvestLotFieldReader.MaxReasonLength);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(lot, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (_, facts) = await edits.FactsAsync(lot, cancellationToken);
        if (facts.RemovalTakesALaneOutOfHarvested())
        {
            // The lanes are no longer all Harvested, so the earlier reminder is no longer true and marking the last one again is a new last lane (BR-CYCLE-07).
            lot.Cycle.CloseRemindedAt = null;
        }

        var keys = lot.Photos.Select(p => p.FileKey).ToList();
        audit.Record(HarvestLotAudit.Entry(HarvestLotAudit.Removed, lot, lot.Raft.ZoneId) with { Before = HarvestLotAudit.Snapshot(lot), Reason = reason });

        // The lane states depend on the cycle row: a parallel lot, close, cancel or seeding correction read it before this removal, so one of them fails with MSG06.
        unitOfWork.MarkChanged(lot.Cycle);
        // The kg the lot gave to its products go back: its Harvest rows are undone by reversal rows, which keep the lot's code and id.
        await productLines.ReverseAsync(lot, cancellationToken);
        // The line of a harvest plan the lot was recorded from is open again, and a plan that had finished by itself is Harvesting again.
        await planProgress.OnLotRemovedAsync(lot, cancellationToken);
        lots.Remove(lot);
        await fileSaver.SaveAsync([], keys, cancellationToken);
        return true;
    }
}
