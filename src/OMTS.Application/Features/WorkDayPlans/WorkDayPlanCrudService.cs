using OMTS.Application.Features.Authorization;
using OMTS.Application.Common;
using OMTS.Application.Common.Concurrency;
using OMTS.Application.Common.Exceptions;
using OMTS.Application.Common.Interfaces;
using OMTS.Application.Common.Interfaces.Persistence;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.Audit;
using OMTS.Application.Features.Audit.Models;
using OMTS.Application.Features.Authorization.Models;
using OMTS.Application.Features.Purchasing;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Farming;
using OMTS.Domain.Entities.Labor;
using OMTS.Domain.Enums;

namespace OMTS.Application.Features.WorkDayPlans;

/// <summary>
/// The work-day plans (Kế hoạch ngày làm). The Owner, and a Zone Manager in its own zones, make, change and cancel the nursery (one zone, optionally with the running batch) and seeding (one raft) plans; a harvest plan is made
/// with its line of a harvest plan (<see cref="WorkDayPlanHarvestLink"/>) and only the Owner changes its number of people, note and time. A plan has one day and one place; the people on it follow it when its day or place changes and
/// go when it is cancelled (<see cref="WorkDayPlanStaffingService"/>). Reads follow the zones of the caller and of the header zone filter.
/// </summary>
public sealed class WorkDayPlanCrudService(
    IWorkDayPlanRepository plans,
    IRaftRepository rafts,
    IFarmingZoneRepository zones,
    INurseryBatchRepository batches,
    IAccountRepository accounts,
    WorkDayPlanAccess access,
    WorkDayPlanViews views,
    WorkDayPlanCodeService codes,
    WorkDayPlanStaffingService staffing,
    WorkDayPlanSettler settler,
    WorkDayPlanNotifier notifier,
    RowVersionGuard rowVersions,
    ICurrentUser user,
    AuditLogService audit,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    private const string RecordType = nameof(WorkDayPlan);
    private const int MaxDays = 62;

    // ---- Reading ----

    public async Task<Result<IReadOnlyList<WorkDayPlanItem>>> ListAsync(WorkDayPlanListQuery query, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null)
        {
            return AppError.Forbidden;
        }

        if (query.From == default || query.To < query.From || query.To.DayNumber - query.From.DayNumber >= MaxDays)
        {
            return AppError.Invalid("Period");
        }

        return await ReadAsync(who, query.From, query.To, query.ZoneIds, query.Kind, query.Status, cancellationToken);
    }

    /// <summary>The plans of one day the caller may staff and that are not over: the plans to invite someone into.</summary>
    public async Task<Result<IReadOnlyList<WorkDayPlanItem>>> DayOptionsAsync(WorkDayPlanDayQuery query, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null)
        {
            return AppError.Forbidden;
        }

        if (query.Date == default)
        {
            return AppError.Invalid("Date");
        }

        var all = await ReadAsync(who, query.Date, query.Date, query.ZoneIds, null, null, cancellationToken);
        return all.IsSuccess
            ? Result<IReadOnlyList<WorkDayPlanItem>>.Success(all.Value!.Where(p => p.Allowed.CanStaff).ToList())
            : all.Error!;
    }

    public async Task<Result<WorkDayPlanItem>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        var plan = who is null ? null : await plans.GetForEditAsync(id, cancellationToken);
        return plan is null || !WorkDayPlanAccess.MaySee(who!, plan) ? AppError.NotFound : await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>The people who can be in charge of a plan in the zone (the caller; for the Owner also the Zone Managers of the zone).</summary>
    public async Task<Result<IReadOnlyList<WorkDayPlanResponsible>>> ResponsiblesAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null || !who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, zoneId))
        {
            return AppError.Forbidden;
        }

        return Result<IReadOnlyList<WorkDayPlanResponsible>>.Success(await access.ResponsiblesAsync(who, zoneId, cancellationToken));
    }

    private async Task<Result<IReadOnlyList<WorkDayPlanItem>>> ReadAsync(
        AccountAccess who, DateOnly from, DateOnly to, Guid[]? zoneIds, WorkDayPlanKind? kind, WorkDayPlanStatus? status, CancellationToken cancellationToken)
    {
        var chosen = PermittedZones.Choose(null, zoneIds);
        if (!chosen.IsSuccess)
        {
            return chosen.Error!;
        }

        var narrowed = who.PermittedZonesFor(PermissionFunctions.WorkAssignments, PermissionAction.View).Narrow(chosen.Value);
        if (narrowed.IsEmpty)
        {
            return Result<IReadOnlyList<WorkDayPlanItem>>.Success([]);
        }

        var found = await plans.ListAsync(new WorkDayPlanFilter(from, to, narrowed.ZoneIds, kind, status), cancellationToken);
        return Result<IReadOnlyList<WorkDayPlanItem>>.Success(await views.ListAsync(found.Where(p => WorkDayPlanAccess.MaySee(who, p)).ToList(), cancellationToken));
    }

    // ---- Making and changing ----

    /// <summary>
    /// A nursery or seeding plan. 400 MSG-GEN-01 naming the wrong field; 403 for a caller who may not create work assignments in the zone; 409 MSG-GEN-02 for a code another plan has, and for a harvest plan (it comes from a line
    /// of a harvest plan). A repeated <c>clientOperationId</c> answers the plan already made.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> CreateAsync(SaveWorkDayPlanRequest request, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        if (who is null)
        {
            return AppError.Forbidden;
        }

        if (request.Kind == WorkDayPlanKind.Harvest)
        {
            return WorkDayPlanStaffingService.Refused("Kế hoạch ngày làm thu hoạch được tạo cùng dòng của kế hoạch thu hoạch (KHT), không tạo riêng.", "HARVEST_FROM_PLAN");
        }

        var invalid = new List<string>();
        if (request.Kind is not (WorkDayPlanKind.Nursery or WorkDayPlanKind.Seeding))
        {
            invalid.Add("Kind");
        }

        var place = request.Kind is null ? null : await PlaceAsync(request.Kind.Value, request.RaftId, request.ZoneId, request.NurseryBatchId, invalid, cancellationToken);
        var date = request.WorkDate;
        if (date is null || date.Value < timeProvider.Today())
        {
            invalid.Add("Work date");
        }

        invalid.AddRange(CommonInvalid(request, date));
        if (invalid.Count > 0 || place is null)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        if (!who.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Create, PermissionScope.ZoneScoped, place.ZoneId))
        {
            return AppError.Forbidden;
        }

        var operationId = request.ClientOperationId is { } op && op != Guid.Empty ? op : Guid.NewGuid();
        if (request.ClientOperationId is not null && await plans.FindOperationForEditAsync(who.AccountId, operationId, cancellationToken) is { } made)
        {
            return await views.DetailAsync(made, cancellationToken);
        }

        var responsible = await ResponsibleAsync(who, request.ResponsibleAccountId, place.ZoneId, cancellationToken);
        if (responsible is null)
        {
            return AppError.Invalid("Plan responsible");
        }

        if (request.Code is { } typed && !string.IsNullOrWhiteSpace(typed) && DocumentCodes.Typed(typed) is null)
        {
            return AppError.InvalidFields(["Code"]);
        }

        var (code, codeError) = await codes.ForNewAsync(request.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var plan = new WorkDayPlan
        {
            Code = code!,
            Kind = request.Kind!.Value,
            Status = WorkDayPlanStatus.Staffing,
            WorkDate = date!.Value,
            StartsAt = request.StartsAt?.ToOffset(BusinessClock.UtcOffset),
            ExpectedEndAt = request.ExpectedEndAt?.ToOffset(BusinessClock.UtcOffset),
            ZoneId = place.ZoneId,
            Zone = place.Zone,
            RaftId = place.Raft?.Id,
            Raft = place.Raft,
            NurseryBatchId = place.Batch?.Id,
            NurseryBatch = place.Batch,
            CreatorAccountId = who.AccountId,
            CreatorAccount = (await accounts.GetByIdAsync(who.AccountId, cancellationToken))!,
            ResponsibleAccountId = responsible.Id,
            ResponsibleAccount = responsible,
            RequiredPeople = request.RequiredPeople!.Value,
            Note = request.Note?.Trim() is { Length: > 0 } note ? note : null,
            ClientOperationId = operationId,
        };
        plans.Add(plan);
        audit.Record(new AuditEntry("WorkDayPlan.Created", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, After = Snapshot(plan),
        });
        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await views.DetailAsync((await plans.GetForEditAsync(plan.Id, cancellationToken))!, cancellationToken);
    }

    /// <summary>
    /// Changes a plan that is not over. A nursery or seeding plan changes its code, day, times, place, person in charge, number of people and note; the people on it follow the new day and place (409 when one checked in, has other
    /// work that day or the day is locked). A harvest plan changes only <c>expectedEndAt</c>, the person in charge, <c>requiredPeople</c> and <c>note</c>, and only for the Owner: its day and raft follow its line.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> UpdateAsync(Guid id, SaveWorkDayPlanRequest request, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        var plan = who is null ? null : await plans.GetForEditAsync(id, cancellationToken);
        if (plan is null || !WorkDayPlanAccess.MaySee(who!, plan))
        {
            return AppError.NotFound;
        }

        if (!WorkDayPlanAccess.MayEdit(who!, plan))
        {
            return AppError.Forbidden;
        }

        if (!WorkDayPlanRules.Open(plan.Status))
        {
            return WorkDayPlanStaffingService.Refused(
                $"Kế hoạch {plan.Code} đang ở trạng thái {WorkDayPlanRules.StatusName(plan.Status)} nên không thể sửa.", "PLAN_CLOSED");
        }

        if (await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        if (request.Kind is { } kind && kind != plan.Kind)
        {
            return AppError.InvalidFields(["Kind"]);
        }

        var harvest = plan.Kind == WorkDayPlanKind.Harvest;
        var invalid = new List<string>();
        var date = harvest ? plan.WorkDate : request.WorkDate;
        var place = harvest ? null : await PlaceAsync(plan.Kind, request.RaftId, request.ZoneId, request.NurseryBatchId, invalid, cancellationToken);
        // A plan that started keeps its day and place: the people on it checked in there.
        var started = plan.Status == WorkDayPlanStatus.InProgress;
        if (!harvest && (date is null || date.Value < timeProvider.Today() && date.Value != plan.WorkDate))
        {
            invalid.Add("Work date");
        }

        invalid.AddRange(CommonInvalid(request, date));
        if (invalid.Count > 0 || !harvest && place is null)
        {
            return AppError.InvalidFields(invalid.Distinct().ToList());
        }

        if (!harvest && !who!.Allows(PermissionFunctions.WorkAssignments, PermissionAction.Edit, PermissionScope.ZoneScoped, place!.ZoneId))
        {
            return AppError.Forbidden;
        }

        var responsible = request.ResponsibleAccountId is null || request.ResponsibleAccountId == plan.ResponsibleAccountId
            ? plan.ResponsibleAccount
            : await ResponsibleAsync(who!, request.ResponsibleAccountId, plan.ZoneId, cancellationToken);
        if (responsible is null)
        {
            return AppError.Invalid("Plan responsible");
        }

        if (request.Code is { } typed && !string.IsNullOrWhiteSpace(typed) && DocumentCodes.Typed(typed) is null)
        {
            return AppError.InvalidFields(["Code"]);
        }

        var (code, codeError) = await codes.ForEditAsync(request.Code, plan.Id, plan.Code, cancellationToken);
        if (codeError is not null)
        {
            return codeError;
        }

        var before = Snapshot(plan);
        if (!harvest)
        {
            var target = place!;
            var newDate = date!.Value;
            var moved = newDate != plan.WorkDate || target.ZoneId != plan.ZoneId || target.Raft?.Id != plan.RaftId;
            if (moved && started)
            {
                return WorkDayPlanStaffingService.Refused($"Kế hoạch {plan.Code} đang làm nên không thể đổi ngày hoặc nơi làm.", "PLAN_STARTED");
            }

            if (moved && await staffing.FollowPlaceAsync(plan, newDate, target.ZoneId, target.Raft?.Id, cancellationToken) is { } refused)
            {
                return refused;
            }

            plan.WorkDate = newDate;
            (plan.ZoneId, plan.Zone) = (target.ZoneId, target.Zone);
            (plan.RaftId, plan.Raft) = (target.Raft?.Id, target.Raft);
            (plan.NurseryBatchId, plan.NurseryBatch) = (target.Batch?.Id, target.Batch);
            plan.StartsAt = request.StartsAt?.ToOffset(BusinessClock.UtcOffset);
        }

        plan.Code = code;
        plan.ExpectedEndAt = request.ExpectedEndAt?.ToOffset(BusinessClock.UtcOffset);
        (plan.ResponsibleAccountId, plan.ResponsibleAccount) = (responsible.Id, responsible);
        plan.Note = request.Note?.Trim() is { Length: > 0 } note ? note : null;
        plan.RequiredPeople = request.RequiredPeople!.Value;
        var (changedBefore, changedAfter) = AuditDiff.Changed(before, Snapshot(plan));
        if (changedAfter is not null)
        {
            audit.Record(new AuditEntry("WorkDayPlan.Updated", RecordType)
            {
                RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, Before = changedBefore, After = changedAfter,
            });
        }

        await settler.SettleAsync(plan, cancellationToken);
        if (await SaveAsync(plan, cancellationToken) is { } error)
        {
            return error;
        }

        return await views.DetailAsync((await plans.GetForEditAsync(plan.Id, cancellationToken))!, cancellationToken);
    }

    /// <summary>Cancels a nursery or seeding plan that is not over: the assignments of its people are removed unless they checked in. 409 for a harvest plan (it is cancelled with its line).</summary>
    public async Task<Result<WorkDayPlanItem>> CancelAsync(Guid id, CancelWorkDayPlanRequest request, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        var plan = who is null ? null : await plans.GetForEditAsync(id, cancellationToken);
        if (plan is null || !WorkDayPlanAccess.MaySee(who!, plan))
        {
            return AppError.NotFound;
        }

        if (plan.Kind == WorkDayPlanKind.Harvest && who!.IsCooperativeOwner)
        {
            return WorkDayPlanStaffingService.Refused($"Kế hoạch {plan.Code} được hủy cùng dòng của kế hoạch thu hoạch.", "HARVEST_FROM_PLAN");
        }

        if (!WorkDayPlanAccess.MayCancel(who!, plan))
        {
            return AppError.Forbidden;
        }

        var reason = request.Reason?.Trim();
        if (reason is { Length: > WorkDayPlanRules.MaxReasonLength })
        {
            return AppError.InvalidFields(["Reason"]);
        }

        if (!WorkDayPlanRules.Open(plan.Status))
        {
            return WorkDayPlanStaffingService.Refused(
                $"Kế hoạch {plan.Code} đang ở trạng thái {WorkDayPlanRules.StatusName(plan.Status)} nên không thể hủy.", "PLAN_CLOSED");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        await staffing.ReleaseAssignmentsAsync(plan, cancellationToken);
        Cancel(plan, string.IsNullOrEmpty(reason) ? null : reason);
        await notifier.CancelledAsync(plan, user.AccountId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    /// <summary>
    /// "Hoàn thành": a nursery or seeding plan that is Ready or running, on its day or after, whose work is done without a record. 409 for a plan that is not Ready or running or whose day has not come; a harvest plan is
    /// completed with its line.
    /// </summary>
    public async Task<Result<WorkDayPlanItem>> CompleteAsync(Guid id, CompleteWorkDayPlanRequest request, CancellationToken cancellationToken)
    {
        var who = await access.CurrentAsync(cancellationToken);
        var plan = who is null ? null : await plans.GetForEditAsync(id, cancellationToken);
        if (plan is null || !WorkDayPlanAccess.MaySee(who!, plan))
        {
            return AppError.NotFound;
        }

        if (!WorkDayPlanAccess.MayStaff(who!, plan))
        {
            return AppError.Forbidden;
        }

        if (plan.Kind == WorkDayPlanKind.Harvest)
        {
            return WorkDayPlanStaffingService.Refused($"Kế hoạch {plan.Code} hoàn thành khi dòng kế hoạch thu hoạch của nó xong.", "HARVEST_FROM_PLAN");
        }

        if (plan.Status is not (WorkDayPlanStatus.Ready or WorkDayPlanStatus.InProgress) || plan.WorkDate > timeProvider.Today())
        {
            return WorkDayPlanStaffingService.Refused(
                $"Kế hoạch {plan.Code} ({WorkDayPlanRules.StatusName(plan.Status)}) chưa thể hoàn thành: cần đủ người và đã đến ngày làm.", "PLAN_NOT_RUNNING");
        }

        if (request.RowVersion is not null && await rowVersions.CheckAsync(plan, request.RowVersion, cancellationToken) is { } stale)
        {
            return stale;
        }

        Complete(plan);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await views.DetailAsync(plan, cancellationToken);
    }

    // ---- Shared with the line of a harvest plan ----

    /// <summary>Moves the plan to Completed, audited. Nothing is saved.</summary>
    internal void Complete(WorkDayPlan plan)
    {
        var from = plan.Status;
        plan.Status = WorkDayPlanStatus.Completed;
        plan.CompletedAt = timeProvider.GetUtcNow();
        unitOfWork.MarkChanged(plan);
        audit.Record(new AuditEntry("WorkDayPlan.StatusChanged", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, Before = new { Status = from.ToString() }, After = new { Status = plan.Status.ToString() },
        });
    }

    /// <summary>Moves the plan to Cancelled with the reason, audited. Nothing is saved.</summary>
    internal void Cancel(WorkDayPlan plan, string? reason)
    {
        var from = plan.Status;
        plan.Status = WorkDayPlanStatus.Cancelled;
        plan.CancelledAt = timeProvider.GetUtcNow();
        plan.CancelReason = reason;
        unitOfWork.MarkChanged(plan);
        audit.Record(new AuditEntry("WorkDayPlan.StatusChanged", RecordType)
        {
            RecordId = plan.Id, RecordCode = plan.Code, ZoneId = plan.ZoneId, Before = new { Status = from.ToString() }, After = new { Status = plan.Status.ToString() }, Reason = reason,
        });
    }

    // ---- Checking ----

    private sealed record Place(Guid ZoneId, FarmingZone Zone, Raft? Raft, Domain.Entities.Cultivation.NurseryBatch? Batch);

    /// <summary>The place of the plan, with what is wrong added to <paramref name="invalid"/>: a raft In Use in a zone In Use for a seeding plan; a zone In Use (and optionally its Nursing batch) for a nursery plan.</summary>
    private async Task<Place?> PlaceAsync(WorkDayPlanKind kind, Guid? raftId, Guid? zoneId, Guid? batchId, List<string> invalid, CancellationToken cancellationToken)
    {
        if (kind == WorkDayPlanKind.Nursery)
        {
            if (raftId is not null)
            {
                invalid.Add("Raft");
            }

            // Tracked, so the plan points at the zone that is there and does not insert it again.
            var zone = zoneId is { } z && z != Guid.Empty ? await zones.GetForEditAsync(z, cancellationToken) : null;
            if (zone is null || zone.StatusId != StatusIds.Usage.InUse)
            {
                invalid.Add("Zone");
                return null;
            }

            Domain.Entities.Cultivation.NurseryBatch? batch = null;
            if (batchId is { } b)
            {
                batch = await batches.GetForEditAsync(b, cancellationToken);
                if (batch is null || batch.ZoneId != zone.Id || batch.StatusId != StatusIds.NurseryBatch.Nursing)
                {
                    invalid.Add("Nursery batch");
                }
            }

            return invalid.Count > 0 ? null : new Place(zone.Id, zone, null, batch);
        }

        if (batchId is not null)
        {
            invalid.Add("Nursery batch");
        }

        var raft = raftId is { } r && r != Guid.Empty ? await rafts.GetForEditAsync(r, cancellationToken) : null;
        if (raft is null || raft.StatusId != StatusIds.Usage.InUse || raft.Zone.StatusId != StatusIds.Usage.InUse)
        {
            invalid.Add("Raft");
            return null;
        }

        return invalid.Count > 0 ? null : new Place(raft.ZoneId, raft.Zone, raft, null);
    }

    private static IEnumerable<string> CommonInvalid(SaveWorkDayPlanRequest request, DateOnly? date)
    {
        if (!WorkDayPlanRules.ValidRequiredPeople(request.RequiredPeople))
        {
            yield return "Required people";
        }

        if (request.Note?.Trim().Length > WorkDayPlanRules.MaxNoteLength)
        {
            yield return "Note";
        }

        if (request.StartsAt is { } start && date is { } day && BusinessClock.ToBusinessDate(start) != day)
        {
            yield return "Starts at";
        }

        if (request.ExpectedEndAt is { } end && (request.StartsAt is { } s && end <= s))
        {
            yield return "Expected end at";
        }
    }

    /// <summary>The account in charge: the caller by default; the Owner may name a Zone Manager of the zone; a Zone Manager names itself.</summary>
    private async Task<Domain.Entities.Identity.Account?> ResponsibleAsync(AccountAccess who, Guid? requested, Guid zoneId, CancellationToken cancellationToken)
    {
        var id = requested ?? who.AccountId;
        if (id != who.AccountId && (!who.IsCooperativeOwner || (await access.ManagersOfZoneAsync(zoneId, cancellationToken)).All(m => m.Id != id)))
        {
            return null;
        }

        var account = await accounts.GetByIdAsync(id, cancellationToken);
        return account?.StatusId == StatusIds.Account.Active ? account : null;
    }

    private static object Snapshot(WorkDayPlan plan) => new
    {
        plan.Code, Kind = plan.Kind.ToString(), plan.WorkDate, plan.StartsAt, plan.ExpectedEndAt, plan.ZoneId, plan.RaftId, plan.NurseryBatchId, plan.ResponsibleAccountId, plan.RequiredPeople, plan.Note,
    };

    /// <summary>The loser of two parallel saves with one code answers MSG-GEN-02.</summary>
    private async Task<AppError?> SaveAsync(WorkDayPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DuplicateRecordException)
        {
            var code = plan.Code;
            var planId = plan.Id;
            unitOfWork.DiscardChanges();
            return await codes.DuplicateAsync(code, planId, cancellationToken) is { } duplicate
                ? duplicate
                : AppError.Conflict(Common.Messages.UserMessages.AlreadyExists(WorkDayPlanCodeService.ObjectName));
        }
    }
}
