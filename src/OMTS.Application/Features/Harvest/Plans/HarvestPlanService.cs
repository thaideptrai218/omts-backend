using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Messages;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Application.Features.Harvest.Plans;

/// <summary>
/// The harvest plans (KHT): the Owner makes one for the rafts of any zones (the zone of a line is the zone of its raft), changes it, hands it to the Zone Managers of every zone of its lines, completes it once a Zone Manager handed it over, or
/// cancels it. A Draft is free to change; once it is handed over only the lines without a lot can change, and lines can be added (the Zone Managers of a zone a change brings in are told).
/// A line is one whole raft and a plan holds no lanes. Lots move the plan along (<see cref="HarvestPlanProgress"/>). The service checks the zones of the lines; the API checks that the
/// role holds the right in some zone.
/// </summary>
public sealed class HarvestPlanService(
    IHarvestPlanRepository plans,
    IRaftRepository rafts,
    ICultivationCycleQuery cycleQuery,
    HarvestPlanAccess access,
    HarvestPlanCodeService codes,
    HarvestPlanViews views,
    HarvestPlanProgress progress,
    HarvestPlanNotifier notifier,
    HarvestPlanStaffingService staffing,
    IStatusCatalog statusCatalog,
    RowVersionGuard rowVersions,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const string RecordType = nameof(HarvestPlan);

    private sealed record Prepared(IReadOnlyDictionary<Guid, Raft> Rafts, IReadOnlyList<SalesOrder> Orders, DateTimeOffset PlannedAt, IReadOnlyDictionary<Guid, Guid> CycleProducts);

    // ---- Creating ----

    public async Task<Result<HarvestPlanSaved>> CreateAsync(SaveHarvestPlanRequest request, CancellationToken cancellationToken)
    {
        var (entries, invalid) = HarvestPlanEntryReader.Read(request);
        if (entries is null)
        {
            return AppError.InvalidFields(invalid);
        }

        var prepared = await PrepareAsync(entries, stored: null, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        if (!await access.MayWriteAsync(prepared.Value!.Rafts.Values.Select(r => r.ZoneId), PermissionAction.Create, cancellationToken))
        {
            return AppError.Forbidden;
        }

        // A typed code is checked before anything is spent; an empty one takes its number once the plan is valid.
        if (entries.Code is { } typed && await codes.DuplicateAsync(typed, null, cancellationToken) is { } taken)
        {
            return taken;
        }

        var (code, codeError) = await codes.ForNewAsync(entries.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var ready = prepared.Value!;
        var me = currentUser.AccountId!.Value;
        var plan = new HarvestPlan
        {
            Code = code!,
            PlannedAt = ready.PlannedAt,
            PlannedDate = BusinessClock.ToBusinessDate(ready.PlannedAt),
            Note = entries.Note,
            CreatedByAccountId = me,
        };
        foreach (var entry in entries.Lines)
        {
            plan.Lines.Add(NewLine(plan, entry, ready.Rafts[entry.RaftId], (ready.CycleProducts.TryGetValue(entry.RaftId, out var product) ? product : (Guid?)null)));
        }

        foreach (var order in ready.Orders)
        {
            plan.Orders.Add(new SalesOrderHarvestPlan { OrderId = order.Id, PlanId = plan.Id });
        }

        plans.Add(plan);
        audit.Record(new AuditEntry("HarvestPlan.Created", RecordType) { RecordId = plan.Id, RecordCode = plan.Code, After = Snapshot(plan, ready.Orders.ToDictionary(o => o.Id, o => o.Code)) });
        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(plan.Id, cancellationToken);
    }

    // ---- Changing ----

    /// <summary>Replaces the header, the orders and the lines of a plan that is not over. After the hand-over, a line with its lot must come back unchanged (MSG-PLAN-03).</summary>
    public async Task<Result<HarvestPlanSaved>> UpdateAsync(Guid planId, SaveHarvestPlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!HarvestPlanRules.AllowsEdit(plan.StatusId))
        {
            return WrongStatus(plan);
        }

        var zonesBefore = HarvestPlanAccess.ZoneIdsOf(plan);
        if (!await access.MayWriteAsync(zonesBefore, PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var (entries, invalid) = HarvestPlanEntryReader.Read(request);
        if (entries is null)
        {
            return AppError.InvalidFields(invalid);
        }

        var isDraft = plan.StatusId == StatusIds.HarvestPlan.Draft;

        if (entries.Lines.Any(l => l.Id is { } id && plan.Lines.All(s => s.Id != id)))
        {
            return AppError.InvalidFields(["Lines"]);
        }

        var prepared = await PrepareAsync(entries, plan, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error!;
        }

        var ready = prepared.Value!;
        if (!await access.MayWriteAsync(ready.Rafts.Values.Select(r => r.ZoneId), PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!isDraft && await CheckHandedOverEditAsync(plan, entries, ready, cancellationToken) is { } refused)
        {
            return refused;
        }

        var (code, codeError) = await codes.ForEditAsync(entries.Code, plan.Id, plan.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var orderCodes = plan.Orders.Where(o => o.Order is not null).ToDictionary(o => o.OrderId, o => o.Order.Code);
        foreach (var order in ready.Orders)
        {
            orderCodes[order.Id] = order.Code;
        }

        // The crew chosen for the lines goes with them: to the new day, and off a line that is taken out or moved to another raft. Checked before the plan changes.
        var newDate = BusinessClock.ToBusinessDate(ready.PlannedAt);
        var leaving = plan.Lines.Where(l => entries.Lines.FirstOrDefault(e => e.Id == l.Id) is not { } kept || kept.RaftId != l.RaftId).Select(l => l.Id).ToHashSet();
        if (await staffing.FollowAsync(plan, newDate, leaving, cancellationToken) is { } staffingRefused)
        {
            return staffingRefused;
        }

        var before = Snapshot(plan, orderCodes);
        var allDoneBefore = HarvestPlanProgress.AllDone(plan);
        plan.Code = code;
        plan.PlannedAt = ready.PlannedAt;
        plan.PlannedDate = newDate;
        plan.Note = entries.Note;
        ApplyOrders(plan, ready.Orders);
        ApplyLines(plan, entries, ready);
        unitOfWork.MarkChanged(plan);
        if (!isDraft)
        {
            // Lines taken out or added can finish the plan, or leave one open; a zone a line was added in is told, as the plan was handed over before it was there.
            progress.Settle(plan, allDoneBefore);
            var brought = HarvestPlanAccess.ZoneIdsOf(plan).Except(zonesBefore).ToList();
            if (brought.Count > 0)
            {
                await notifier.AssignedAsync(plan, brought, currentUser.AccountId!.Value, cancellationToken);
            }
        }

        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(plan, orderCodes));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("HarvestPlan.Updated", RecordType) { RecordId = plan.Id, RecordCode = plan.Code, Before = changedBefore, After = changedAfter });
        }

        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(plan.Id, cancellationToken);
    }

    /// <summary>"Giao QLV": the Draft is handed to the Zone Managers of its zone, who are told. Every raft needs an Open cycle.</summary>
    public async Task<Result<HarvestPlanSaved>> AssignAsync(Guid planId, HarvestPlanActionRequest request, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(HarvestPlanAccess.ZoneIdsOf(plan), PermissionAction.Edit, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!HarvestPlanRules.AllowsAssign(plan.StatusId))
        {
            return WrongStatus(plan);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (plan.Lines.Count == 0)
        {
            return AppError.InvalidFields(["Lines"]);
        }

        foreach (var line in plan.Lines.OrderBy(l => l.LineNumber))
        {
            if (await RaftRefusalAsync(line.Raft, cancellationToken) is { } refused)
            {
                return refused;
            }
        }

        var me = currentUser.AccountId!.Value;
        plan.AssignedAt = timeProvider.GetUtcNow();
        plan.AssignedByAccountId = me;
        progress.Move(plan, StatusIds.HarvestPlan.Assigned);
        await notifier.AssignedAsync(plan, HarvestPlanAccess.ZoneIdsOf(plan), me, cancellationToken);
        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(plan.Id, cancellationToken);
    }

    /// <summary>"Hoàn thành": only the Owner, only a plan a Zone Manager handed over ("Bàn giao": every line has its lot). The system never completes a plan by itself.</summary>
    public async Task<Result<HarvestPlanSaved>> FinishAsync(Guid planId, HarvestPlanActionRequest request, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.IsOwnerAsync(cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (!HarvestPlanRules.AllowsFinish(plan.StatusId))
        {
            return WrongStatus(plan);
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var left = plan.Lines.Where(l => !HarvestPlanRules.LineDone(HarvestPlanRules.FactsOf(l))).ToList();
        progress.Move(plan, StatusIds.HarvestPlan.Completed);
        audit.Record(new AuditEntry("HarvestPlan.Finished", RecordType)
        {
            RecordId = plan.Id,
            RecordCode = plan.Code,
            After = new { LinesLeft = left.Count, RaftsLeft = left.Select(l => l.Raft.Code).ToList() },
        });
        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(plan.Id, cancellationToken);
    }

    /// <summary>Cancels a Draft, an Assigned or a Received plan with no lot, with a reason. A plan that has started is handed over and completed instead (MSG-PLAN-04).</summary>
    public async Task<Result<HarvestPlanSaved>> CancelAsync(Guid planId, CancelHarvestPlanRequest request, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null || !await access.MayReadAsync(plan, cancellationToken))
        {
            return AppError.NotFound;
        }

        if (!await access.MayWriteAsync(HarvestPlanAccess.ZoneIdsOf(plan), PermissionAction.Delete, cancellationToken))
        {
            return AppError.Forbidden;
        }

        if (plan.StatusId is StatusIds.HarvestPlan.Harvesting or StatusIds.HarvestPlan.HandedOver)
        {
            return AppError.Conflict(UserMessages.PlanCannotBeCancelled(plan.Code));
        }

        if (!HarvestPlanRules.AllowsCancel(plan.StatusId))
        {
            return WrongStatus(plan);
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > HarvestPlanRules.MaxNoteLength)
        {
            return AppError.Invalid("Reason");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (plan.Lines.Any(l => l.Lot is not null))
        {
            return AppError.Conflict(UserMessages.PlanCannotBeCancelled(plan.Code));
        }

        if (await staffing.FollowAsync(plan, plan.PlannedDate, plan.Lines.Select(l => l.Id).ToHashSet(), cancellationToken) is { } staffingRefused)
        {
            return staffingRefused;
        }

        plan.CancelledAt = timeProvider.GetUtcNow();
        plan.CancelledByAccountId = currentUser.AccountId;
        plan.CancelReason = reason;
        var from = plan.StatusId;
        plan.StatusId = StatusIds.HarvestPlan.Cancelled;
        unitOfWork.MarkChanged(plan);
        audit.RecordStatusChange(RecordType, plan.Id, plan.Code, from, plan.StatusId, reason);
        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await SavedAsync(plan.Id, cancellationToken);
    }

    // ---- Checking ----

    /// <summary>
    /// What the entries name, checked against the database: the rafts (of any zone) and the orders. The rafts come tracked, with their zone;
    /// the product of each raft is that of its Open cycle.
    /// </summary>
    private async Task<Result<Prepared>> PrepareAsync(HarvestPlanEntries entries, HarvestPlan? stored, CancellationToken cancellationToken)
    {
        // A plan is made for sales orders (MSG-PLAN-07): one plan may serve many orders, but never none.
        if (entries.SalesOrderIds.Count == 0)
        {
            return AppError.Conflict(UserMessages.PlanNeedsSalesOrder(), new Dictionary<string, object?> { ["field"] = "SalesOrderIds" });
        }

        var invalid = new List<string>();
        var now = timeProvider.GetUtcNow();
        var plannedAt = entries.PlannedAt.ToOffset(BusinessClock.UtcOffset);

        var keptRafts = stored?.Lines.Select(l => l.RaftId).ToHashSet() ?? [];
        var raftById = new Dictionary<Guid, Raft>();
        foreach (var raftId in entries.Lines.Select(l => l.RaftId).Distinct())
        {
            if (await rafts.GetForEditAsync(raftId, cancellationToken) is { } raft)
            {
                raftById[raftId] = raft;
            }
        }

        foreach (var entry in entries.Lines)
        {
            var field = $"Line {entry.LineNumber}";
            // A raft (and its zone) that left use since stays on a plan that already has it.
            if (!raftById.TryGetValue(entry.RaftId, out var raft)
                || ((raft.StatusId != StatusIds.Usage.InUse || raft.Zone.StatusId != StatusIds.Usage.InUse) && !keptRafts.Contains(raft.Id)))
            {
                invalid.Add($"{field} raft");
            }
        }

        // A line is one whole raft, and a raft is on a plan once. A plan stored before that rule keeps the lines it has on the same raft (they may hold lots), but no line may join them.
        var storedRaftOf = stored?.Lines.ToDictionary(l => l.Id, l => l.RaftId) ?? new Dictionary<Guid, Guid>();
        foreach (var group in entries.Lines.Where(l => l.RaftId != Guid.Empty).GroupBy(l => l.RaftId).Where(g => g.Count() > 1))
        {
            var kept = group.Where(l => l.Id is { } id && storedRaftOf.TryGetValue(id, out var storedRaft) && storedRaft == l.RaftId).ToList();
            foreach (var repeated in group.Except(kept).Skip(kept.Count == 0 ? 1 : 0))
            {
                invalid.Add($"Line {repeated.LineNumber} raft");
            }
        }

        var linked = stored?.Orders.Select(o => o.OrderId).ToHashSet() ?? [];
        var orders = await plans.OrdersByIdsAsync(entries.SalesOrderIds, cancellationToken);
        if (orders.Count != entries.SalesOrderIds.Count
            || orders.Any(o => !linked.Contains(o.Id) && o.StatusId is not (StatusIds.SalesOrder.Draft or StatusIds.SalesOrder.Delivering)))
        {
            invalid.Add("Sales orders");
        }

        // One order has at most one active plan (MSG-PLAN-08); a plan may serve many orders. A cancelled plan frees its orders.
        if (invalid.Count == 0 && orders.Count > 0)
        {
            var existing = await plans.PlansOfOrdersAsync(orders.Select(o => o.Id).ToList(), cancellationToken);
            var taken = existing.FirstOrDefault(p => p.StatusId != StatusIds.HarvestPlan.Cancelled && p.PlanId != stored?.Id);
            if (taken is not null)
            {
                var orderCode = orders.First(o => o.Id == taken.OrderId).Code;
                return AppError.Conflict(UserMessages.PlanOrderAlreadyPlanned(orderCode, taken.PlanCode), new Dictionary<string, object?> { ["field"] = "SalesOrderIds" });
            }
        }

        if (invalid.Count > 0)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        return new Prepared(raftById, orders, plannedAt, await plans.CycleProductsAsync(raftById.Keys.ToList(), cancellationToken));
    }

    /// <summary>The raft of a line can be harvested: In Use in a zone In Use, with an Open cycle.</summary>
    private async Task<AppError?> RaftRefusalAsync(Raft raft, CancellationToken cancellationToken)
    {
        if (raft.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Raft {raft.Code}", statusCatalog.DisplayNameOf(raft.StatusId)));
        }

        if (raft.Zone.StatusId != StatusIds.Usage.InUse)
        {
            return AppError.Conflict(UserMessages.WrongStatus($"Farming zone {raft.Zone.Code}", statusCatalog.DisplayNameOf(raft.Zone.StatusId)));
        }

        return await cycleQuery.OpenCycleAsync(raft.Id, cancellationToken) is null
            ? AppError.Conflict(UserMessages.WrongStatus($"Raft {raft.Code}", "without an Open cycle"))
            : null;
    }

    /// <summary>
    /// A plan handed to the Zone Managers: a line with its lot must come back as it is (MSG-PLAN-03), every other line is free, and a line that is new or moved to another raft needs an Open cycle.
    /// </summary>
    private async Task<AppError?> CheckHandedOverEditAsync(HarvestPlan plan, HarvestPlanEntries entries, Prepared ready, CancellationToken cancellationToken)
    {
        foreach (var line in plan.Lines.Where(l => HarvestPlanRules.LineLocked(HarvestPlanRules.FactsOf(l))))
        {
            var entry = entries.Lines.FirstOrDefault(e => e.Id == line.Id);
            if (entry is null || entry.RaftId != line.RaftId || entry.ExpectedCageCount != line.ExpectedCageCount || entry.Note != line.Note)
            {
                return AppError.Conflict(UserMessages.PlanLineAlreadyHarvested(plan.Code, line.Raft.Code), new Dictionary<string, object?> { ["lineId"] = line.Id });
            }
        }

        foreach (var entry in entries.Lines)
        {
            var stored = entry.Id is { } id ? plan.Lines.FirstOrDefault(l => l.Id == id) : null;
            if (stored is null || stored.RaftId != entry.RaftId)
            {
                if (await RaftRefusalAsync(ready.Rafts[entry.RaftId], cancellationToken) is { } refused)
                {
                    return refused;
                }
            }
        }

        return null;
    }

    // ---- Applying ----

    private static HarvestPlanLine NewLine(HarvestPlan plan, HarvestPlanLineEntries entry, Raft raft, Guid? cycleProductId) => new()
    {
        PlanId = plan.Id,
        LineNumber = entry.LineNumber,
        RaftId = raft.Id,
        Raft = raft,
        ExpectedProductId = cycleProductId,
        ExpectedCageCount = entry.ExpectedCageCount,
        Note = entry.Note,
    };

    private void ApplyOrders(HarvestPlan plan, IReadOnlyList<SalesOrder> wanted)
    {
        var wantedIds = wanted.Select(o => o.Id).ToHashSet();
        foreach (var link in plan.Orders.Where(o => !wantedIds.Contains(o.OrderId)).ToList())
        {
            plan.Orders.Remove(link);
            plans.RemoveOrderLink(link);
        }

        var have = plan.Orders.Select(o => o.OrderId).ToHashSet();
        foreach (var order in wanted.Where(o => !have.Contains(o.Id)))
        {
            var link = new SalesOrderHarvestPlan { OrderId = order.Id, PlanId = plan.Id };
            plan.Orders.Add(link);
            plans.AddOrderLink(link);
        }
    }

    /// <summary>Lines are matched by id: a kept line takes the new fields, a line not sent is taken out (it has no lot, or the check before refused), a line with no id is new. Lines are numbered as sent.</summary>
    private void ApplyLines(HarvestPlan plan, HarvestPlanEntries entries, Prepared ready)
    {
        var unused = plan.Lines.ToList();
        foreach (var entry in entries.Lines)
        {
            var raft = ready.Rafts[entry.RaftId];
            var line = entry.Id is { } id ? unused.FirstOrDefault(l => l.Id == id) : null;
            if (line is null)
            {
                var added = NewLine(plan, entry, raft, (ready.CycleProducts.TryGetValue(raft.Id, out var addedProduct) ? addedProduct : (Guid?)null));
                plan.Lines.Add(added);
                plans.AddLine(added);
                continue;
            }

            unused.Remove(line);
            line.LineNumber = entry.LineNumber;
            // The product follows the raft's cycle; a line of the same raft keeps the one it has when the cycle names none (a cycle started before products were recorded), another raft starts with the one of its own cycle.
            line.ExpectedProductId = ready.CycleProducts.TryGetValue(raft.Id, out var cycleProduct) ? cycleProduct : line.RaftId == raft.Id ? line.ExpectedProductId : null;
            line.ExpectedCageCount = entry.ExpectedCageCount;
            line.Note = entry.Note;
            line.RaftId = raft.Id;
            line.Raft = raft;
        }

        foreach (var removed in unused)
        {
            plan.Lines.Remove(removed);
            plans.RemoveLine(removed);
        }
    }

    // ---- Saving ----

    /// <summary>The one save of a plan. The loser of two parallel saves with one code answers MSG-GEN-02.</summary>
    private async Task<AppError?> SaveAsync(HarvestPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            var code = plan.Code;
            var id = plan.Id;
            unitOfWork.DiscardChanges();
            return await codes.DuplicateAsync(code, id, cancellationToken) is { } duplicate
                ? duplicate
                : AppError.Conflict(UserMessages.AlreadyExists(HarvestPlanCodeService.ObjectName));
        }
    }

    private async Task<Result<HarvestPlanSaved>> SavedAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await plans.GetForEditAsync(planId, cancellationToken);
        if (plan is null)
        {
            return AppError.NotFound;
        }

        var saved = UserMessages.Saved($"{HarvestPlanCodeService.ObjectName} {plan.Code}");
        return new HarvestPlanSaved(await views.ViewAsync(plan, cancellationToken), saved.Text, saved.Code);
    }

    private static object Snapshot(HarvestPlan plan, IReadOnlyDictionary<Guid, string> orderCodes) => new
    {
        plan.Code,
        plan.PlannedAt,
        plan.Note,
        Orders = plan.Orders.Select(o => orderCodes.GetValueOrDefault(o.OrderId) ?? o.OrderId.ToString()).Order().ToList(),
        Lines = plan.Lines.OrderBy(l => l.LineNumber).Select(l => new
        {
            l.LineNumber,
            RaftCode = l.Raft?.Code,
            l.ExpectedProductId,
            l.ExpectedCageCount,
            l.Note,
        }).ToList(),
    };

    private AppError WrongStatus(HarvestPlan plan) =>
        AppError.Conflict(UserMessages.WrongStatus($"{HarvestPlanCodeService.ObjectName} {plan.Code}", statusCatalog.DisplayNameOf(plan.StatusId)));
}
