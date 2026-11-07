using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;

namespace OMTS.Application.Features.Harvest;

/// <summary>
/// HARVEST-01.1 step 3: removes a lot with a reason, while its cycle is Open and it is on no sales invoice (BR-HARVEST-05): a Zone Manager what they recorded, the Cooperative Owner any lot. It stays only
/// in the audit log, its lanes are worked out from the other lots, and its code is not given again. A sale in the same moment marks the lot changed, so the row version makes one of them fail.
/// </summary>
public sealed class HarvestLotRemovalService(
    IHarvestLotRepository lots,
    IHarvestLotSalesQuery sales,
    HarvestLotEditReader edits,
    RowVersionGuard rowVersions,
    UploadedFileSaver fileSaver,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
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
        lots.Remove(lot);
        await fileSaver.SaveAsync([], keys, cancellationToken);
        return true;
    }
}
