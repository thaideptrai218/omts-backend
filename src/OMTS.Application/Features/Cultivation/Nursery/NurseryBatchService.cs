using System.Globalization;
using OMTS.Application.Common;
using OMTS.Application.Common.Constants;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Cultivation.Seeding;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Files;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>
/// NURSERY-01 start a nursery batch (ươm giống, decision of 12/11/2026), for a Zone Manager of the zone and the Cooperative Owner. The nursery is a zone, not a raft; the batch takes ONE seed lot, the
/// pieces leave the stock in the same save (a use issue, MSG-INPUT-01 409 with the balance when they are not on hand) and 1 to 3 photos are required. The photos are stored first: when one cannot be
/// uploaded, or the save is refused, nothing is kept and nobody is told. A Zone Manager's start notifies the Cooperative Owner. The receipt row of the lot is part of the save, so a parallel return or
/// cancellation of the receipt, or another batch or session that uses the lot at once, loses with MSG06.
/// </summary>
public sealed class NurseryBatchService(
    INurseryBatchRepository nurseries,
    IFarmingZoneRepository zones,
    SeedingSessionReader seedLots,
    NurseryStockSync stock,
    IDocumentNumberAllocator numbers,
    RecordPhotoStore photoStore,
    UploadedFileSaver fileSaver,
    NurseryBatchViews views,
    NurseryNotifier notifier,
    ZoneAccessGuard zoneAccess,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<Result<NurseryBatchSaved>> StartAsync(Guid zoneId, StartNurseryRequest request, IReadOnlyList<IncomingFile> photos, CancellationToken cancellationToken)
    {
        var zone = await zones.GetForEditAsync(zoneId, cancellationToken);
        if (zone is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Create, zone.Id, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.NotUsableChoice($"Farming zone {zone.Code}", "Discontinued", NurseryValues.ZoneField);
        }

        var today = timeProvider.Today();
        var check = new FieldCheck();
        var lot = await seedLots.LotAsync(request.SeedLotId, null, cancellationToken);
        check.Check(lot is not null, NurseryValues.SeedLotField);
        check.Check(NurseryRules.IsPieces(request.Quantity), NurseryValues.QuantityField);
        var start = request.StartDate ?? today;
        check.Check(start <= today && (lot is null || start >= lot.Receipt.ReceiptDate), NurseryValues.StartDateField);
        var note = check.Optional(request.Note, NurseryValues.NoteField, NurseryBatch.MaxNoteLength);
        if (!check.IsValid)
        {
            return check.ToError();
        }

        var files = StepPhotoService.Read(photos, otherFilePart: request.OtherFilePart);
        if (!files.IsSuccess)
        {
            return files.Error!;
        }

        var quantity = request.Quantity!.Value;
        if (await stock.CheckOnHandAsync(lot!, quantity, 0m, cancellationToken) is { } notOnHand)
        {
            return notOnHand;
        }

        var actor = currentUser.AccountId ?? throw new InvalidOperationException("A nursery batch needs an authenticated caller.");
        var uploaded = await photoStore.UploadAllAsync(FileCategories.NurseryStartPhotos, files.Value!, cancellationToken);
        if (!uploaded.IsSuccess)
        {
            return uploaded.Error!;
        }

        var prepared = await photoStore.PrepareSaveAsync(uploaded.Value!, () => PrepareAsync(zone, lot!, quantity, start, note, uploaded.Value!, actor, cancellationToken));
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        await fileSaver.SaveAsync(uploaded.Value!, [], cancellationToken);
        var saved = UserMessages.Saved("Nursery batch");
        return new NurseryBatchSaved(await views.ViewAsync(prepared.Value!, cancellationToken), saved.Text, saved.Code);
    }

    private async Task<Result<NurseryBatch>> PrepareAsync(
        FarmingZone zone, PurchaseReceiptLine lot, decimal quantity, DateOnly start, string? note, IReadOnlyList<string> keys, Guid actor,
        CancellationToken cancellationToken)
    {
        var number = await numbers.NextAsync(DocumentSequenceKeys.NurseryBatch, cancellationToken);
        var batch = new NurseryBatch
        {
            Code = $"{DocumentSequenceKeys.NurseryBatch}{number.ToString("000000", CultureInfo.InvariantCulture)}",
            ZoneId = zone.Id, Zone = zone, SeedLotLineId = lot.Id, SeedLotLine = lot, QuantityIn = quantity, StartDate = start, Note = note,
        };
        nurseries.Add(batch);
        NurseryPhotoRows.Add(nurseries, batch, NurseryPhotoPhase.Start, keys);
        if (await stock.PostAsync(batch, lot, cancellationToken) is { } stockRefused)
        {
            return stockRefused;
        }

        unitOfWork.MarkChanged(lot.Receipt);
        audit.Record(NurseryValues.Entry(NurseryValues.Created, batch) with { After = NurseryValues.Snapshot(batch) });
        await notifier.StartedAsync(batch, actor, cancellationToken);
        return batch;
    }
}
