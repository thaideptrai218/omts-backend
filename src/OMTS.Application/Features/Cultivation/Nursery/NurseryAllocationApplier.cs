using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Cultivation.Cycles;
using OMTS.Application.Features.Farming;
using OMTS.Application.Features.Farming.Rafts;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Application.Features.Cultivation.Nursery;

/// <summary>
/// The shares of the survivors between the rafts of the zone, for the completion (NURSERY-03), the correction of the survivors and the transfer between rafts (NURSERY-04): one rule set, so the
/// three never disagree. Every raft gets one cycle from the batch, whose <c>ExpectedSeedQuantity</c> and <c>NurseryAllocatedQuantity</c> are its share; the shares add up to the survivors exactly.
/// A share can move or go only while its cycle is Open and the seed hung is kept (share not below it; a raft with seed on it stays); a Closed cycle's share is frozen. A raft added gets a new cycle,
/// a raft taken out loses its cycle (kept only in the audit log, as a cycle cancelled by mistake). Nothing is saved: the caller saves the whole change, and a refusal leaves nothing behind.
/// </summary>
public sealed class NurseryAllocationApplier(
    IRaftRepository rafts,
    ICultivationCycleRepository cycles,
    ICultivationCycleQuery cycleQuery,
    INurseryBatchRepository nurseries,
    RaftViews raftViews,
    AuditLogService audit,
    IUnitOfWork unitOfWork)
{
    private const int TargetYieldScale = 2;

    /// <param name="batch">Tracked, with its zone and its allocations (raft and cycle).</param>
    /// <param name="survivors">The survivors the shares must add up to.</param>
    /// <param name="newCycleStart">The start date of the cycle of a raft that has none yet.</param>
    internal async Task<AppError?> ApplyAsync(NurseryBatch batch, decimal survivors, IReadOnlyList<NurseryAllocationInput> inputs, DateOnly newCycleStart, CancellationToken cancellationToken)
    {
        if (NurseryRules.AllocationHint(survivors, inputs) is { } hint)
        {
            return AppError.Invalid(NurseryValues.AllocationsField, hint);
        }

        var check = new FieldCheck();
        var targets = inputs.Select(i => check.PositiveOptional(i.TargetYieldKg, NurseryValues.TargetYieldField, TargetYieldScale)).ToList();
        if (!check.IsValid)
        {
            return check.ToError();
        }

        var seeded = await nurseries.SeededByCycleAsync(batch.Allocations.Select(a => a.CycleId).ToList(), cancellationToken);
        var wanted = inputs.Select((input, index) => (input.RaftId!.Value, Quantity: input.Quantity!.Value, Target: targets[index])).ToDictionary(w => w.Item1);

        foreach (var allocation in batch.Allocations.ToList())
        {
            var done = wanted.TryGetValue(allocation.RaftId, out var share)
                ? Keep(allocation, share.Quantity, share.Target, seeded.GetValueOrDefault(allocation.CycleId))
                : await RemoveAsync(batch, allocation, seeded.GetValueOrDefault(allocation.CycleId), cancellationToken);
            if (done is not null)
            {
                return done;
            }
        }

        foreach (var share in wanted.Values.Where(w => batch.Allocations.All(a => a.RaftId != w.Item1)))
        {
            if (await AddAsync(batch, share.Item1, share.Quantity, share.Target, newCycleStart, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        return null;
    }

    /// <summary>A raft that keeps a share: the new quantity is not below the seed hung, and a Closed cycle does not change.</summary>
    private AppError? Keep(NurseryBatchAllocation allocation, decimal quantity, decimal? target, decimal seeded)
    {
        var open = allocation.Cycle.StatusId == StatusIds.CultivationCycle.Open;
        if (!open)
        {
            return quantity == allocation.Quantity && target is null ? null : Frozen(allocation);
        }

        if (quantity < seeded)
        {
            return NurseryRules.Refused(
                $"Bè {allocation.Raft.Code} đã thả {seeded:0.###} mảnh, không thể chia ít hơn ({quantity:0.###} mảnh).", NurseryReasonCodes.AllocationBelowSeeded,
                new Dictionary<string, object?> { ["field"] = NurseryValues.AllocationsField, ["raftId"] = allocation.RaftId, ["seeded"] = seeded });
        }

        if (quantity != allocation.Quantity || target is not null)
        {
            allocation.Quantity = quantity;
            (allocation.Cycle.NurseryAllocatedQuantity, allocation.Cycle.ExpectedSeedQuantity) = (quantity, quantity);
            allocation.Cycle.TargetYieldKg = target ?? allocation.Cycle.TargetYieldKg;
            unitOfWork.MarkChanged(allocation);
            unitOfWork.MarkChanged(allocation.Cycle);
        }

        return null;
    }

    /// <summary>A raft that loses its share: its cycle goes too, so it must be Open with no seed and no record.</summary>
    private async Task<AppError?> RemoveAsync(NurseryBatch batch, NurseryBatchAllocation allocation, decimal seeded, CancellationToken cancellationToken)
    {
        var cycle = allocation.Cycle;
        if (cycle.StatusId != StatusIds.CultivationCycle.Open)
        {
            return Frozen(allocation);
        }

        if (seeded > 0 || await cycles.CountRecordsAsync(cycle.Id, cancellationToken) > 0)
        {
            return NurseryRules.Refused(
                $"Bè {allocation.Raft.Code} đã có thả giống hoặc nhật ký chăm sóc, không thể bỏ khỏi đợt ươm.", NurseryReasonCodes.AllocationBelowSeeded,
                new Dictionary<string, object?> { ["field"] = NurseryValues.AllocationsField, ["raftId"] = allocation.RaftId, ["seeded"] = seeded });
        }

        audit.Record(new AuditEntry("CultivationCycle.Cancelled", nameof(CultivationCycle)) { RecordId = cycle.Id, RecordCode = allocation.Raft.Code, ZoneId = batch.ZoneId, Before = CultivationCycleValues.Snapshot(cycle) });
        batch.Allocations.Remove(allocation);
        nurseries.RemoveAllocation(allocation);
        cycles.Remove(cycle);
        return null;
    }

    /// <summary>A raft of the zone, In Use, with no Open cycle and not closed after the start date: it gets a new cycle holding its share.</summary>
    private async Task<AppError?> AddAsync(NurseryBatch batch, Guid raftId, decimal quantity, decimal? target, DateOnly start, CancellationToken cancellationToken)
    {
        var raft = await rafts.GetForEditAsync(raftId, cancellationToken);
        if (raft is null)
        {
            return AppError.Invalid(NurseryValues.AllocationsField, "Bè không tồn tại.");
        }

        var details = new Dictionary<string, object?> { ["field"] = NurseryValues.AllocationsField, ["raftId"] = raft.Id, ["raftCode"] = raft.Code };
        if (raft.ZoneId != batch.ZoneId)
        {
            return NurseryRules.Refused($"Bè {raft.Code} không thuộc vùng {batch.Zone.Code} của đợt ươm.", NurseryReasonCodes.RaftOtherZone, details);
        }

        if (raftViews.EditRefusal(raft) is { } refused)
        {
            return refused;
        }

        if (await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is { } open)
        {
            details["cycleId"] = open.Id;
            return NurseryRules.Refused($"Bè {raft.Code} đang có chu kỳ nuôi mở. Hãy chọn bè khác hoặc chốt chu kỳ đó trước.", NurseryReasonCodes.RaftHasOpenCycle, details);
        }

        if (await cycles.LastCloseDateAsync(raft.Id, cancellationToken) is { } previousClose && start <= previousClose)
        {
            details["previousCloseDate"] = previousClose;
            return NurseryRules.Refused(
                $"Chu kỳ trước của bè {raft.Code} chốt ngày {previousClose:dd/MM/yyyy}; chu kỳ mới phải bắt đầu sau ngày đó.", NurseryReasonCodes.CycleStartsBeforeClose, details);
        }

        var cycle = new CultivationCycle
        {
            RaftId = raft.Id, Raft = raft, StartDate = start, ExpectedSeedQuantity = quantity, TargetYieldKg = target, NurseryBatchId = batch.Id, NurseryAllocatedQuantity = quantity,
        };
        cycles.Add(cycle);
        var allocation = new NurseryBatchAllocation { NurseryBatchId = batch.Id, NurseryBatch = batch, RaftId = raft.Id, Raft = raft, CycleId = cycle.Id, Cycle = cycle, Quantity = quantity };
        batch.Allocations.Add(allocation);
        nurseries.AddAllocation(allocation);

        // The raft row is part of the save: a parallel start of a cycle or a lane change read "no open cycle", so one of the two fails with MSG06.
        unitOfWork.MarkChanged(raft);
        audit.Record(new AuditEntry("CultivationCycle.Started", nameof(CultivationCycle)) { RecordId = cycle.Id, RecordCode = raft.Code, ZoneId = batch.ZoneId, After = CultivationCycleValues.Snapshot(cycle) });
        return null;
    }

    private static AppError Frozen(NurseryBatchAllocation allocation) => NurseryRules.Refused(
        $"Chu kỳ của bè {allocation.Raft.Code} đã chốt, không thể đổi phần chia.", NurseryReasonCodes.AllocationFrozen,
        new Dictionary<string, object?> { ["field"] = NurseryValues.AllocationsField, ["raftId"] = allocation.RaftId });
}
