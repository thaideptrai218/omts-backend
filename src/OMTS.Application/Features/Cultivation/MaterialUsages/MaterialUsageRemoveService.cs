using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Authorization;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;

namespace OMTS.Application.Features.Cultivation.MaterialUsages;

/// <summary>
/// CYCLE-03.2 remove a usage record with a reason: same people and locks as an edit (a Zone Manager of the raft's zone or the Cooperative Owner; the raft In Use; no closed cycle on the date,
/// BR-CYCLE-04). Its quantity returns to the stock: the linked issue (Xuat dung) is Cancelled in the same save and the audit log keeps the record with the reason. 409 MSG-GEN-09 when a closed
/// cycle locks it.
/// </summary>
public sealed class MaterialUsageRemoveService(
    IMaterialUsageRepository usages,
    MaterialUsageCycleGuard cycleGuard,
    MaterialUsageStockSync stockSync,
    RaftViews raftViews,
    ZoneAccessGuard zoneAccess,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    public async Task<Result<bool>> RemoveAsync(Guid usageId, RemoveMaterialUsageRequest request, CancellationToken cancellationToken)
    {
        var usage = await usages.GetForEditAsync(usageId, cancellationToken);
        if (usage is null)
        {
            return AppError.NotFound;
        }

        if (!await zoneAccess.AllowsAsync(PermissionFunctions.CultivationCycles, PermissionAction.Edit, usage.Raft.ZoneId, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if ((raftViews.EditRefusal(usage.Raft) ?? await cycleGuard.RefusalAsync(usage.Raft, usage.UsageDate, cancellationToken)) is { } refused)
        {
            return refused;
        }

        var check = new FieldCheck();
        var reason = MaterialUsageValues.CheckReason(check, request.Reason);
        if (!check.IsValid)
        {
            return AppError.InvalidFields(check.Invalid);
        }

        if (await rowVersions.CheckAsync(usage, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        // The record may not leave a cycle that closes now: the guarding row is part of this save.
        if (await cycleGuard.HoldAsync(usage.Raft, usage.UsageDate, cancellationToken) is { } lost)
        {
            return lost;
        }

        audit.Record(MaterialUsageValues.Entry(MaterialUsageValues.Removed, usage, usage.Raft) with
        {
            Before = MaterialUsageValues.Snapshot(usage, usage.Raft, usage.Item), Reason = reason,
        });
        await stockSync.RemoveAsync(usage, usage.Raft, reason, cancellationToken);
        usages.Remove(usage);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
