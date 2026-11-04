using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>
/// CYCLE-03.1 edit a usage record with a reason. Any Zone Manager of the raft's zone and the Cooperative Owner, not only who recorded it: BR-CYCLE-03 has no own-record rule. The
/// raft must be In Use (GBR-01) and no closed cycle may lock the record (BR-CYCLE-04). The edit may move the record to another raft In Use, in a zone the caller also has access to.
/// A record carries a version (NFR-09 excepts only logs and water measurements).
/// </summary>
public sealed class MaterialUsageEditService(
    IMaterialUsageRepository usages,
    IRaftRepository rafts,
    IFarmingInputItemRepository items,
    MaterialUsageCycleGuard cycleGuard,
    RaftViews raftViews,
    MaterialUsageViews views,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    IStatusCatalog statusCatalog,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public async Task<Result<MaterialUsageSaved>> EditAsync(Guid usageId, EditMaterialUsageRequest request, CancellationToken cancellationToken)
    {
        var usage = await usages.GetForEditAsync(usageId, cancellationToken);
        if (usage is null)
        {
            return AppError.NotFound;
        }

        if (!await MayEditAsync(usage.Raft, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if ((raftViews.EditRefusal(usage.Raft) ?? await cycleGuard.RefusalAsync(usage.Raft, usage.UsageDate, cancellationToken)) is { } refused)
        {
            return refused;
        }

        var target = request.RaftId == usage.RaftId ? usage.Raft : request.RaftId is { } raftId ? await rafts.GetForEditAsync(raftId, cancellationToken) : null;
        if (target is not null && !await MayEditAsync(target, cancellationToken))
        {
            return AppError.Forbidden;
        }

        var itemChanges = request.ItemId != usage.ItemId;
        var item = !itemChanges ? usage.Item : request.ItemId is { } itemId ? await items.GetForEditAsync(itemId, cancellationToken) : null;

        var check = new FieldCheck();
        check.Check(target is not null, MaterialUsageValues.RaftField);
        MaterialUsageValues.CheckDate(check, request.UsageDate, timeProvider.Today());
        // The item stays as it is without a new check: only a new choice must be an item of the group In Use (GBR-01).
        check.Check(!itemChanges || MaterialUsageValues.IsMaterial(item), MaterialUsageValues.ItemField);
        MaterialUsageValues.CheckQuantity(check, request.Quantity, MaterialUsageValues.QuantityField);
        var repairReason = MaterialUsageValues.CheckRepairReason(check, request.RepairReason);
        var reason = MaterialUsageValues.CheckReason(check, request.Reason);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(usage, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var date = request.UsageDate!.Value;
        if (raftViews.EditRefusal(target!) is { } wrongRaft)
        {
            return wrongRaft;
        }

        if (itemChanges && MaterialUsageValues.IsDiscontinued(item!))
        {
            return AppError.NotUsableChoice($"Input item {item!.Code}", statusCatalog.DisplayNameOf(item.StatusId), MaterialUsageValues.ItemField);
        }

        if (await cycleGuard.RefusalAsync(target!, date, cancellationToken) is { } closed)
        {
            return closed;
        }

        var (sourceRaft, sourceDate) = (usage.Raft, usage.UsageDate);
        var before = MaterialUsageValues.Snapshot(usage, sourceRaft, usage.Item);
        (usage.Raft, usage.RaftId, usage.UsageDate, usage.Item, usage.ItemId, usage.Quantity, usage.RepairReason) =
            (target!, target!.Id, date, item!, item!.Id, request.Quantity!.Value, repairReason);
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, MaterialUsageValues.Snapshot(usage, target, item));
        if (changedAfter is not null)
        {
            // Both ends of the edit are guarded: the record may not leave a cycle that closes now, nor enter one.
            if ((await cycleGuard.HoldAsync(sourceRaft, sourceDate, cancellationToken) ?? await cycleGuard.HoldAsync(target, date, cancellationToken)) is { } lost)
            {
                return lost;
            }

            audit.Record(MaterialUsageValues.Entry(MaterialUsageValues.Updated, usage, target) with { Before = changedBefore, After = changedAfter, Reason = reason });
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var message = UserMessages.Saved("Material usage");
        return new MaterialUsageSaved(await views.RecordAsync(usage.Id, cancellationToken), message.Text, message.Code);
    }

    private Task<bool> MayEditAsync(Raft raft, CancellationToken cancellationToken) =>
        zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, raft.ZoneId, cancellationToken);
}
